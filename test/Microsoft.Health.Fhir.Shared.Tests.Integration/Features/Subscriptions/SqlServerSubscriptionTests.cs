// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Compartment;
using Microsoft.Health.Fhir.Core.Features.FhirPath;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Messages.Delete;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Extensions;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Subscriptions;
using Microsoft.Health.Fhir.SqlServer.Features.Watchdogs;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.Integration.Persistence;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.Integration.Features.Subscriptions
{
    /// <summary>
    /// Integration tests for the subscription store and event pump against SQL Server (ADR-2611).
    /// </summary>
    [FhirStorageTestsFixtureArgumentSets(DataStore.SqlServer)]
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SqlServerSubscriptionTests : IClassFixture<FhirStorageTestsFixture>, IAsyncLifetime
    {
        private const string TopicUrl = "http://example.org/topic/encounter-cancelled";

        private readonly FhirStorageTestsFixture _fixture;
        private readonly SqlServerFhirStorageTestsFixture _sql;
        private readonly SqlServerSubscriptionStore _store;
        private readonly StubConverter _converter = new();
        private readonly StubLoader _loader = new();
        private readonly SubscriptionsConfiguration _configuration = new() { Enabled = true };

        private readonly ITestOutputHelper _output;

        public SqlServerSubscriptionTests(FhirStorageTestsFixture fixture, ITestOutputHelper output)
        {
            _output = output;
            _fixture = fixture;
            _sql = (SqlServerFhirStorageTestsFixture)fixture.Service;
            _store = new SqlServerSubscriptionStore(_sql.SqlRetryService, _sql.SqlServerFhirModel, NullLogger<SqlServerSubscriptionStore>.Instance);
        }

        public ValueTask InitializeAsync()
        {
            ExecuteSql("DELETE FROM dbo.SubscriptionEvents; DELETE FROM dbo.SubscriptionStates; DELETE FROM dbo.Parameters WHERE Id = '" + SqlServerSubscriptionStore.CursorId + "'");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        [Fact]
        public async Task GivenAGrant_WhenTheContentHashChanges_ThenTheActivationIsCleared()
        {
            // Arrange
            await _store.SaveGrantAsync("s1", "hash-a", "{}", CancellationToken.None);
            long activation = await _store.ActivateAsync("s1", CancellationToken.None);

            // Act
            await _store.SaveGrantAsync("s1", "hash-a", "{\"a\":1}", CancellationToken.None);
            SubscriptionState sameContent = await _store.GetStateAsync("s1", CancellationToken.None);
            await _store.SaveGrantAsync("s1", "hash-b", "{}", CancellationToken.None);
            SubscriptionState newContent = await _store.GetStateAsync("s1", CancellationToken.None);

            // Assert
            Assert.Equal(activation, sameContent.ActivationTransactionId);
            Assert.Equal("{\"a\":1}", sameContent.Grant);
            Assert.Null(newContent.ActivationTransactionId);
            Assert.Equal("hash-b", newContent.ContentHash);
        }

        [Fact]
        public async Task GivenAnActivatedSubscription_WhenActivatedAgain_ThenTheWatermarkIsKept()
        {
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            long first = await _store.ActivateAsync("s1", CancellationToken.None);
            await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();

            long second = await _store.ActivateAsync("s1", CancellationToken.None);
            await _store.DeactivateAsync("s1", CancellationToken.None);
            long third = await _store.ActivateAsync("s1", CancellationToken.None);

            Assert.Equal(first, second);
            Assert.True(third > first);
        }

        [Fact]
        public async Task GivenEvents_WhenEnqueuedAndReplayed_ThenNumbersAreContiguousAndNotDuplicated()
        {
            // Arrange
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            await _store.SaveGrantAsync("s2", "h", "{}", CancellationToken.None);
            ExecuteSql("UPDATE dbo.SubscriptionStates SET ActivationTransactionId = 100");
            IReadOnlyList<SubscriptionEventCandidate> batch =
            [
                Candidate("s1", 101, 1), Candidate("s2", 101, 1), Candidate("s1", 102, 2), Candidate("s1", 100, 3), Candidate("s1", 103, 4),
            ];

            // Act: the second call simulates a crash after commit and a replay of the same batch.
            await _store.EnqueueAsync(batch, 103, CancellationToken.None);
            await _store.EnqueueAsync(batch, 103, CancellationToken.None);

            // Assert
            IReadOnlyList<SubscriptionEventRecord> s1 = await _store.GetPendingEventsAsync("s1", 100, CancellationToken.None);
            Assert.Equal([1L, 2L, 3L], s1.Select(e => e.EventNumber).ToArray());
            Assert.Equal([101L, 102L, 103L], s1.Select(e => e.TransactionId).ToArray());
            Assert.Single(await _store.GetPendingEventsAsync("s2", 100, CancellationToken.None));
            Assert.Equal(3, (await _store.GetStateAsync("s1", CancellationToken.None)).LastEventNumber);
            Assert.Equal(103, await _store.GetCursorAsync(CancellationToken.None));
        }

        [Fact]
        public async Task GivenAnInactiveSubscription_WhenEventsAreEnqueued_ThenTheyAreDropped()
        {
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);

            await _store.EnqueueAsync([Candidate("s1", 101, 1), Candidate("unknown", 101, 2)], 101, CancellationToken.None);

            Assert.Empty(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Empty(await _store.GetPendingEventsAsync("unknown", 10, CancellationToken.None));
        }

        [Fact]
        public async Task GivenACursor_WhenAnOlderPositionIsCommitted_ThenItNeverMovesBackwards()
        {
            await _store.EnqueueAsync([], 500, CancellationToken.None);
            await _store.EnqueueAsync([], 400, CancellationToken.None);

            Assert.Equal(500, await _store.GetCursorAsync(CancellationToken.None));
        }

        [Fact]
        public async Task GivenALease_WhenAnotherWorkerTries_ThenItIsExclusiveUntilReleasedOrExpired()
        {
            Assert.True(await _store.TryAcquireLeaseAsync("s1", "w1", TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.True(await _store.TryAcquireLeaseAsync("s1", "w1", TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.False(await _store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromMinutes(1), CancellationToken.None));

            await _store.ReleaseLeaseAsync("s1", "w2", CancellationToken.None);
            Assert.False(await _store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromMinutes(1), CancellationToken.None));

            await _store.ReleaseLeaseAsync("s1", "w1", CancellationToken.None);
            Assert.True(await _store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromSeconds(1), CancellationToken.None));

            ExecuteSql("UPDATE dbo.SubscriptionStates SET LeaseEndDate = dateadd(second,-5,getUTCdate()) WHERE SubscriptionId = 's1'");
            Assert.True(await _store.TryAcquireLeaseAsync("s1", "w3", TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        [Fact]
        public async Task GivenPendingEvents_WhenAttemptsFailAndSucceed_ThenStatusAttemptsAndDueTimesAreTracked()
        {
            // Arrange
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            ExecuteSql("UPDATE dbo.SubscriptionStates SET ActivationTransactionId = 100");
            await _store.EnqueueAsync([Candidate("s1", 101, 1), Candidate("s1", 102, 2), Candidate("s1", 103, 3, SubscriptionEventStatus.Unevaluable)], 103, CancellationToken.None);

            // Act
            await _store.RecordFailedAttemptAsync("s1", [1], DateTimeOffset.UtcNow.AddMinutes(5), "HTTP 503", CancellationToken.None);
            await _store.SetEventStatusAsync("s1", [2], SubscriptionEventStatus.Delivered, CancellationToken.None);

            // Assert
            SubscriptionEventRecord first = Assert.Single(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(1, first.Attempts);
            Assert.Empty(await _store.GetSubscriptionsWithDueEventsAsync(CancellationToken.None));
            Assert.Equal(new SubscriptionEventCounts(1, 1), await _store.GetEventCountsAsync("s1", CancellationToken.None));

            // Activation makes retained events due again.
            await _store.ActivateAsync("s1", CancellationToken.None);
            Assert.Equal(["s1"], await _store.GetSubscriptionsWithDueEventsAsync(CancellationToken.None));
            Assert.Equal(0, Assert.Single(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None)).Attempts);
        }

        [Fact]
        public async Task GivenOldNonPendingEvents_WhenCleanedUp_ThenOnlyPendingAndRecentEventsRemain()
        {
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            ExecuteSql("UPDATE dbo.SubscriptionStates SET ActivationTransactionId = 100");
            await _store.EnqueueAsync([Candidate("s1", 101, 1), Candidate("s1", 102, 2), Candidate("s1", 103, 3)], 103, CancellationToken.None);
            await _store.SetEventStatusAsync("s1", [1, 2], SubscriptionEventStatus.Delivered, CancellationToken.None);
            ExecuteSql("UPDATE dbo.SubscriptionEvents SET StatusDate = dateadd(day,-30,getUTCdate()) WHERE EventNumber IN (1,3)");

            await _store.CleanupEventsAsync(7, CancellationToken.None);

            Assert.Equal(2, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents"));
            Assert.Equal(0, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents WHERE EventNumber = 1"));
        }

        [Fact]
        public async Task GivenASubscription_WhenItsStateIsDeleted_ThenItsEventsAreDeletedToo()
        {
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            ExecuteSql("UPDATE dbo.SubscriptionStates SET ActivationTransactionId = 100");
            await _store.EnqueueAsync([Candidate("s1", 101, 1)], 101, CancellationToken.None);
            await _store.SetErrorAsync("s1", "boom", CancellationToken.None);
            Assert.Equal("boom", (await _store.GetStateAsync("s1", CancellationToken.None)).Error);

            await _store.DeleteStateAsync("s1", CancellationToken.None);

            Assert.Null(await _store.GetStateAsync("s1", CancellationToken.None));
            Assert.Equal(0, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents"));
        }

        [Fact]
        public async Task GivenRapidUpdatesBeforeThePumpRuns_WhenPumped_ThenEachWrittenVersionIsMatchedExactly()
        {
            // Arrange
            SubscriptionEventWatchdog pump = CreatePump("%current.status = 'final' and (%previous.empty() or %previous.status != 'final')");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None); // initializes the cursor
            await ActivateSubscriptionAsync("s1");

            // Act: v1 preliminary, v2 final (becomes history), v3 amended, all committed before matching runs.
            string id = await CreateObservationAsync("preliminary");
            await UpdateObservationAsync(id, "final");
            await UpdateObservationAsync(id, "amended");
            await AdvanceVisibilityAsync();
            int processed = await pump.PumpAsync(CancellationToken.None);
            _output.WriteLine(Dump($"SELECT Version, IsHistory, IsDeleted, TransactionId, datalength(RawResource) FROM dbo.Resource WHERE ResourceId = '{id}'"));

            // Assert: only the version 2 transition matched, even though it was already history.
            Assert.True(processed >= 3);
            SubscriptionEventRecord e = Assert.Single(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(id, e.ResourceId);
            Assert.Equal(2, e.ResourceVersion);
            Assert.Equal(SubscriptionInteraction.Update, e.Interaction);
            Assert.Equal(1, e.EventNumber);
        }

        [Fact]
        public async Task GivenChangesBeforeActivation_WhenPumped_ThenOnlyLaterChangesAreMatched()
        {
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            string before = await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();

            await ActivateSubscriptionAsync("s1");
            string after = await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);

            SubscriptionEventRecord e = Assert.Single(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(after, e.ResourceId);
            Assert.NotEqual(before, e.ResourceId);
        }

        [Fact]
        public async Task GivenASubscriptionActivatedBeforeThePumpFirstRuns_WhenPumped_ThenChangesAfterTheActivationAreMatched()
        {
            // Arrange: activation happens before the pump has a cursor, and a change commits before the pump's first run.
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await ActivateSubscriptionAsync("s1");
            string id = await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();

            // Act
            await pump.PumpAsync(CancellationToken.None); // initializes the cursor
            await pump.PumpAsync(CancellationToken.None);

            // Assert
            SubscriptionEventRecord e = Assert.Single(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(id, e.ResourceId);
        }

        [Fact]
        public async Task GivenADelete_WhenPumped_ThenADeleteEventIsRecorded()
        {
            SubscriptionEventWatchdog pump = CreatePump("true", SubscriptionInteraction.Delete);
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            await ActivateSubscriptionAsync("s1");

            string id = await CreateObservationAsync("preliminary");
            await _fixture.Mediator.DeleteResourceAsync(new ResourceKey("Observation", id), DeleteOperation.SoftDelete);
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);

            SubscriptionEventRecord e = Assert.Single(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(SubscriptionInteraction.Delete, e.Interaction);
        }

        [Fact]
        public async Task GivenABacklogLargerThanOnePage_WhenPumped_ThenTheCursorReachesTheWatermark()
        {
            // Arrange: GetTransactions returns at most 10,000 rows, so the pump must page.
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            long start = (await _store.GetCursorAsync(CancellationToken.None)).Value;
            ExecuteSql($@"
INSERT INTO dbo.Transactions (SurrogateIdRangeFirstValue, SurrogateIdRangeLastValue, IsCompleted, IsSuccess, IsVisible, EndDate, VisibleDate)
SELECT {start} + 10 * n, {start} + 10 * n + 9, 1, 1, 1, getUTCdate(), getUTCdate()
  FROM (SELECT TOP 10050 n = row_number() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects A CROSS JOIN sys.all_objects B) N");
            long visibility = await _sql.SqlServerFhirDataStore.StoreClient.MergeResourcesGetTransactionVisibilityAsync(CancellationToken.None);

            // Act
            int processed = await pump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.True(processed >= 10050, $"processed={processed}");
            Assert.Equal(visibility, await _store.GetCursorAsync(CancellationToken.None));
        }

        [Fact]
        public async Task GivenAnUnversionedResourceType_WhenUpdatedBeforeThePumpRuns_ThenEarlierVersionsAreLostAndTheLatestIsUnevaluable()
        {
            // Documents the ADR-2611 history limit: with no-version, the previous row is deleted during the next write,
            // so the earlier change cannot be matched, and the next one is recorded as unevaluable rather than dropped silently.
            SubscriptionEventWatchdog pump = CreatePump("Organization", "%previous.name != %current.name", []);
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            await ActivateSubscriptionAsync("s1");

            string id = Guid.NewGuid().ToString();
            await CreateAsync("Organization", id, "a");
            await CreateAsync("Organization", id, "b");
            await CreateAsync("Organization", id, "c");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);

            Assert.Empty(await _store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            SubscriptionEventRecord unevaluable = Assert.Single(await _store.GetEventsAsync("s1", SubscriptionEventStatus.Unevaluable, 10, CancellationToken.None));
            Assert.Equal(3, unevaluable.ResourceVersion);
            Assert.Equal(new SubscriptionEventCounts(0, 1), await _store.GetEventCountsAsync("s1", CancellationToken.None));
        }

        private SubscriptionEventWatchdog CreatePump(string fhirPathCriteria, params SubscriptionInteraction[] interactions) => CreatePump("Observation", fhirPathCriteria, interactions);

        private SubscriptionEventWatchdog CreatePump(string triggerResource, string fhirPathCriteria, SubscriptionInteraction[] interactions)
        {
            _converter.Topic = new SubscriptionTopicInfo
            {
                ResourceId = "t1",
                Canonical = TopicUrl,
                Status = "active",
                ResourceTriggers = [new TopicResourceTrigger { Resource = triggerResource, FhirPathCriteria = fhirPathCriteria, SupportedInteractions = interactions }],
            };
            _converter.Subscription = new SubscriptionInfo
            {
                Id = "s1",
                VersionId = "1",
                Status = SubscriptionStatusCode.Active,
                TopicCanonical = TopicUrl,
                ChannelType = "rest-hook",
                Endpoint = "https://hooks.example.com/notify",
                ContentType = "application/fhir+json",
            };

            IOptions<SubscriptionsConfiguration> options = Options.Create(_configuration);
            var fhirPath = new FhirPathCriteriaEvaluator(new FirelyFhirPathProvider());
            var searchEvaluator = new InMemorySearchEvaluator(Substitute.For<IExpressionParser>(), Substitute.For<ISearchIndexer>(), Substitute.For<ICompartmentIndexer>());
            var channel = Substitute.For<ISubscriptionChannel>();
            channel.ChannelType.Returns("rest-hook");
            var validator = new SubscriptionValidator(searchEvaluator, fhirPath, new SubscriptionEndpointPolicy(options), options, ModelInfoProvider.Instance, [channel]);
            var registry = new SubscriptionRegistry(_loader, _converter, _store, validator, NullLogger<SubscriptionRegistry>.Instance);
            var authorizer = new SubscriptionAuthorizer(Options.Create(new SecurityConfiguration()), searchEvaluator);
            var processor = new SubscriptionEventProcessor(new SubscriptionMatcher(searchEvaluator, fhirPath), authorizer, XUnitLogger<SubscriptionEventProcessor>.Create(_output));

            return new SubscriptionEventWatchdog(
                _sql.SqlServerFhirDataStore.StoreClient,
                _store,
                registry,
                processor,
                _sql.SqlServerFhirModel,
                new CompressedRawResourceConverter(),
                _fixture.Deserializer,
                _fixture.FhirRequestContextAccessor,
                _sql.SchemaInformation,
                options,
                _sql.SqlRetryService,
                XUnitLogger<SubscriptionEventWatchdog>.Create(_output));
        }

        private async Task ActivateSubscriptionAsync(string id)
        {
            await _store.SaveGrantAsync(id, _converter.Subscription.ComputeContentHash(), new SubscriptionGrant { ContentHash = _converter.Subscription.ComputeContentHash() }.Serialize(), CancellationToken.None);
            await AdvanceVisibilityAsync();
            await _store.ActivateAsync(id, CancellationToken.None);
        }

        private async Task<string> CreateObservationAsync(string status) => await CreateAsync("Observation", Guid.NewGuid().ToString(), status);

        private async Task UpdateObservationAsync(string id, string status) => await CreateAsync("Observation", id, status);

        private async Task<string> CreateAsync(string type, string id, string status)
        {
            string json = type == "Observation"
                ? $"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"{status}\",\"code\":{{\"text\":\"test\"}}}}"
                : $"{{\"resourceType\":\"{type}\",\"id\":\"{id}\",\"name\":\"{status}\"}}";
            await _fixture.Mediator.UpsertResourceAsync(new Hl7.Fhir.Serialization.FhirJsonParser().Parse<Resource>(json).ToResourceElement());
            return id;
        }

        private async Task AdvanceVisibilityAsync()
        {
            await _sql.SqlServerFhirDataStore.StoreClient.MergeResourcesAdvanceTransactionVisibilityAsync(CancellationToken.None);
        }

        private static SubscriptionEventCandidate Candidate(string subscriptionId, long transactionId, long surrogateId, SubscriptionEventStatus status = SubscriptionEventStatus.Pending)
        {
            return new SubscriptionEventCandidate(
                subscriptionId,
                new ResourceChange
                {
                    TransactionId = transactionId,
                    ResourceTypeId = 1,
                    ResourceSurrogateId = surrogateId,
                    ResourceType = "Encounter",
                    ResourceId = "e" + surrogateId,
                    Version = 1,
                    LastModified = DateTimeOffset.UtcNow,
                },
                status);
        }

        private void ExecuteSql(string sql)
        {
            using var conn = new SqlConnection(_sql.TestConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            cmd.ExecuteNonQuery();
        }

        private string Dump(string sql)
        {
            using var conn = new SqlConnection(_sql.TestConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn);
            using SqlDataReader reader = cmd.ExecuteReader();
            var sb = new System.Text.StringBuilder();
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    sb.Append(reader.GetValue(i)).Append(' ');
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private T ExecuteScalar<T>(string sql)
        {
            using var conn = new SqlConnection(_sql.TestConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn);
            return (T)cmd.ExecuteScalar();
        }

        private sealed class StubConverter : ISubscriptionModelConverter
        {
            public SubscriptionTopicInfo Topic { get; set; }

            public SubscriptionInfo Subscription { get; set; }

            public string TopicResourceType => "Basic";

            public IReadOnlyList<Tuple<string, string>> TopicSearchParameters => [];

            public bool IsSupported => true;

            public bool IsTopic(ResourceElement resource) => resource.Id == "t1";

            public SubscriptionTopicInfo ToTopic(ResourceElement resource) => Topic;

            public SubscriptionInfo ToSubscription(ResourceElement resource) => Subscription;

            public void ApplyServerValues(ResourceElement resource, SubscriptionStatusCode status, DateTimeOffset? end)
            {
            }

            public ResourceElement WithStatus(ResourceElement resource, SubscriptionStatusCode status, string error) => resource;
        }

        private sealed class StubLoader : ISubscriptionResourceLoader
        {
            public Task<IReadOnlyList<ResourceElement>> GetSubscriptionsAsync(CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<ResourceElement>>([new Basic { Id = "s1" }.ToResourceElement()]);

            public Task<IReadOnlyList<ResourceElement>> GetTopicsAsync(CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyList<ResourceElement>>([new Basic { Id = "t1" }.ToResourceElement()]);

            public Task<ResourceElement> GetSubscriptionAsync(string id, CancellationToken cancellationToken) => Task.FromResult<ResourceElement>(null);

            public Task<ResourceElement> GetResourceAsync(string resourceType, string id, CancellationToken cancellationToken) => Task.FromResult<ResourceElement>(null);

            public Task<ResourceElement> GetResourceVersionAsync(string resourceType, string id, int version, CancellationToken cancellationToken) => Task.FromResult<ResourceElement>(null);
        }
    }
}
