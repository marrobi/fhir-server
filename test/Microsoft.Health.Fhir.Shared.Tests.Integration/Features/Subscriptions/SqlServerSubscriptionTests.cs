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
    public class SqlServerSubscriptionTests : SqlServerSubscriptionTestBase, IClassFixture<FhirStorageTestsFixture>
    {
        public SqlServerSubscriptionTests(FhirStorageTestsFixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
        }

        [Fact]
        public async Task GivenAnActivatedSubscription_WhenActivatedAgain_ThenTheWatermarkIsKept()
        {
            await Store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            long first = await Store.ActivateAsync("s1", CancellationToken.None);
            await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();

            long second = await Store.ActivateAsync("s1", CancellationToken.None);
            await Store.DeactivateAsync("s1", revokeGrant: false, CancellationToken.None);
            long third = await Store.ActivateAsync("s1", CancellationToken.None);

            Assert.Equal(first, second);
            Assert.True(third > first);
        }

        [Fact]
        public async Task GivenEvents_WhenEnqueuedAndReplayed_ThenNumbersAreContiguousAndNotDuplicated()
        {
            // Arrange
            await ActivateAtAsync("s1");
            await ActivateAtAsync("s2");
            IReadOnlyList<SubscriptionEventCandidate> batch =
            [
                Candidate("s1", 101, 1), Candidate("s2", 101, 1), Candidate("s1", 102, 2), Candidate("s1", 100, 3), Candidate("s1", 103, 4),
            ];

            // Act: the second call simulates a crash after commit and a replay of the same batch.
            await Store.EnqueueAsync(batch, SqlServerSubscriptionStore.CursorId, 103, null, CancellationToken.None);
            await Store.EnqueueAsync(batch, SqlServerSubscriptionStore.CursorId, 103, null, CancellationToken.None);

            // Assert
            IReadOnlyList<SubscriptionEventRecord> s1 = await Store.GetPendingEventsAsync("s1", 100, CancellationToken.None);
            Assert.Equal([1L, 2L, 3L], s1.Select(e => e.EventNumber).ToArray());
            Assert.Equal([101L, 102L, 103L], s1.Select(e => e.TransactionId).ToArray());
            Assert.Single(await Store.GetPendingEventsAsync("s2", 100, CancellationToken.None));
            Assert.Equal(3, (await Store.GetStateAsync("s1", CancellationToken.None)).LastEventNumber);
            Assert.Equal(103, await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None));
        }

        [Fact]
        public async Task GivenAnInactiveSubscription_WhenEventsAreEnqueued_ThenTheyAreDropped()
        {
            await Store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);

            await Store.EnqueueAsync([Candidate("s1", 101, 1), Candidate("unknown", 101, 2)], SqlServerSubscriptionStore.CursorId, 101, null, CancellationToken.None);

            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Empty(await Store.GetPendingEventsAsync("unknown", 10, CancellationToken.None));
        }

        [Fact]
        public async Task GivenACursor_WhenAnOlderPositionIsCommitted_ThenItNeverMovesBackwards()
        {
            await Store.EnqueueAsync([], SqlServerSubscriptionStore.CursorId, 500, null, CancellationToken.None);
            await Store.EnqueueAsync([], SqlServerSubscriptionStore.CursorId, 400, null, CancellationToken.None);

            Assert.Equal(500, await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None));
        }

        [Fact]
        public async Task GivenASubscriptionActivatedAfterTheStatesWereRead_WhenEventsAreEnqueued_ThenNothingIsWrittenAndTheCallerMustRetry()
        {
            // Arrange: the pump reads the counter, then a handshake activates s2 before the pump commits.
            await ActivateAtAsync("s1");
            await Store.EnqueueAsync([], SqlServerSubscriptionStore.CursorId, 100, null, CancellationToken.None);
            long version = await Store.GetActivationVersionAsync(CancellationToken.None);
            await ActivateAtAsync("s2");

            // Act
            bool committed = await Store.EnqueueAsync([Candidate("s1", 101, 1)], SqlServerSubscriptionStore.CursorId, 101, version, CancellationToken.None);
            bool retried = await Store.EnqueueAsync([Candidate("s1", 101, 1)], SqlServerSubscriptionStore.CursorId, 101, await Store.GetActivationVersionAsync(CancellationToken.None), CancellationToken.None);

            // Assert
            Assert.False(committed);
            Assert.True(retried);
            Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(101, await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None));
        }

        [Fact]
        public async Task GivenALeaseHeldByAnotherWorker_WhenItExpires_ThenItCanBeAcquired()
        {
            // Exclusivity and release are covered by the store contract tests.
            Assert.True(await Store.TryAcquireLeaseAsync("s1", "w1", TimeSpan.FromMinutes(1), CancellationToken.None));

            ExecuteSql("UPDATE dbo.SubscriptionStates SET LeaseEndDate = dateadd(second,-5,getUTCdate()) WHERE SubscriptionId = 's1'");

            Assert.True(await Store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        [Fact]
        public async Task GivenOldNonPendingEvents_WhenCleanedUp_ThenOnlyPendingAndRecentEventsRemain()
        {
            await ActivateAtAsync("s1");
            await Store.EnqueueAsync([Candidate("s1", 101, 1), Candidate("s1", 102, 2), Candidate("s1", 103, 3)], SqlServerSubscriptionStore.CursorId, 103, null, CancellationToken.None);
            await Store.SetEventStatusAsync("s1", [1, 2], SubscriptionEventStatus.Delivered, CancellationToken.None);
            ExecuteSql("UPDATE dbo.SubscriptionEvents SET StatusDate = dateadd(day,-30,getUTCdate()) WHERE EventNumber IN (1,3)");

            bool startedWithoutBudget = await Store.CleanupEventsAsync(7, TimeSpan.Zero, CancellationToken.None);
            Assert.False(startedWithoutBudget);
            Assert.Equal(3, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents"));

            Assert.True(await Store.CleanupEventsAsync(7, null, CancellationToken.None));

            Assert.Equal(2, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents"));
            Assert.Equal(0, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents WHERE EventNumber = 1"));
        }

        [Fact]
        public async Task GivenMoreEventsThanOneDeleteBatch_WhenTheStateIsDeleted_ThenAllEventsAndTheStateAreRemoved()
        {
            // Arrange: more events than one 4,000-row batch.
            await ActivateAtAsync("s1");
            await Store.EnqueueAsync(Enumerable.Range(1, 4500).Select(i => Candidate("s1", 100 + i, i)).ToList(), SqlServerSubscriptionStore.CursorId, 4600, null, CancellationToken.None);
            Assert.Equal(4500, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1'"));

            // Act
            await Store.DeleteStateAsync("s1", CancellationToken.None);

            // Assert
            Assert.Null(await Store.GetStateAsync("s1", CancellationToken.None));
            Assert.Equal(0, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1'"));
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
            Output.WriteLine(Dump($"SELECT Version, IsHistory, IsDeleted, TransactionId, datalength(RawResource) FROM dbo.Resource WHERE ResourceId = '{id}'"));

            // Assert: only the version 2 transition matched, even though it was already history.
            Assert.True(processed >= 3);
            SubscriptionEventRecord e = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
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

            SubscriptionEventRecord e = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(after, e.ResourceId);
            Assert.NotEqual(before, e.ResourceId);
        }

        [Fact]
        public async Task GivenATransactionCommittedBelowTheCursorAfterThePumpPassedIt_WhenChecked_ThenItIsReportedAsLate()
        {
            // Arrange: the first read records when checking starts.
            await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();
            long position = await Sql.SqlServerFhirDataStore.StoreClient.MergeResourcesGetTransactionVisibilityAsync(CancellationToken.None);
            TransactionsEventSource source = CreateTransactionsSource();
            await source.ReadAsync(position, 1, null, CancellationToken.None);
            await Task.Delay(50);

            // A transaction whose id is 10 s below the cursor commits now, as after a backward clock step.
            long late = position - 800_000_000 + 7;
            ExecuteSql($"INSERT INTO dbo.Transactions (SurrogateIdRangeFirstValue, SurrogateIdRangeLastValue, IsCompleted, IsSuccess, IsVisible, EndDate) VALUES ({late}, {late}, 1, 1, 0, getUTCdate())");
            await Task.Delay(50);

            try
            {
                // Act
                await source.ReadAsync(position, 1, null, CancellationToken.None);

                // Assert
                Metrics.Received(1).LateTransactions(1);
            }
            finally
            {
                ExecuteSql($"DELETE FROM dbo.Transactions WHERE SurrogateIdRangeFirstValue = {late}");
            }
        }

        [Fact]
        public async Task GivenAReadLag_WhenPumped_ThenTransactionsYoungerThanTheLagAreReadOnlyLater()
        {
            // Arrange
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            await ActivateSubscriptionAsync("s1");
            string id = await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();
            long visibility = await Sql.SqlServerFhirDataStore.StoreClient.MergeResourcesGetTransactionVisibilityAsync(CancellationToken.None);
            Configuration.TransactionReadLagSeconds = 3600;

            // Act
            await pump.PumpAsync(CancellationToken.None);
            long? heldCursor = await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None);
            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));

            Configuration.TransactionReadLagSeconds = 0;
            await pump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.True(heldCursor < visibility, $"cursor={heldCursor} visibility={visibility}");
            SubscriptionEventRecord e = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(id, e.ResourceId);
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
            SubscriptionEventRecord e = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
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
            await Fixture.Mediator.DeleteResourceAsync(new ResourceKey("Observation", id), DeleteOperation.SoftDelete);
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);

            SubscriptionEventRecord e = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(SubscriptionInteraction.Delete, e.Interaction);
        }

        [Fact]
        public async Task GivenABacklogLargerThanOnePage_WhenPumped_ThenTheCursorReachesTheWatermark()
        {
            // Arrange: GetTransactions returns at most 10,000 rows, so the pump must page.
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            long start = (await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None)).Value;
            ExecuteSql($@"
INSERT INTO dbo.Transactions (SurrogateIdRangeFirstValue, SurrogateIdRangeLastValue, IsCompleted, IsSuccess, IsVisible, EndDate, VisibleDate)
SELECT {start} + 10 * n, {start} + 10 * n + 9, 1, 1, 1, getUTCdate(), getUTCdate()
  FROM (SELECT TOP 10050 n = row_number() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects A CROSS JOIN sys.all_objects B) N");
            long visibility = await Sql.SqlServerFhirDataStore.StoreClient.MergeResourcesGetTransactionVisibilityAsync(CancellationToken.None);

            // Act
            int processed = await pump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.True(processed >= 10050, $"processed={processed}");
            Assert.Equal(visibility, await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None));
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

            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            SubscriptionEventRecord unevaluable = Assert.Single(await Store.GetEventsAsync("s1", SubscriptionEventStatus.Unevaluable, 10, CancellationToken.None));
            Assert.Equal(3, unevaluable.ResourceVersion);
            Assert.Equal(new SubscriptionEventCounts(0, 1), await Store.GetEventCountsAsync("s1", CancellationToken.None));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GivenAnImportThatLoadsAnEarlierVersion_WhenPumped_ThenItNotifiesOnlyWhenHistoricalVersionsAreEnabled(bool notifyHistoricalVersions)
        {
            // Arrange
            Configuration.NotifyHistoricalVersions = notifyHistoricalVersions;
            SubscriptionEventWatchdog pump = CreatePump("Patient", "true", [SubscriptionInteraction.Create, SubscriptionInteraction.Update]);
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            await ActivateSubscriptionAsync("s1");

            string id = Guid.NewGuid().ToString("N");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await ImportPatientAsync(id, now.AddHours(-1));
            await ImportPatientAsync(id, now);
            await ImportPatientAsync(id, now.AddHours(-2)); // older than the current version, so stored as history
            await AdvanceVisibilityAsync();

            // Act
            await pump.PumpAsync(CancellationToken.None);

            // Assert
            List<int> versions = (await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None)).Where(e => e.ResourceId == id).Select(e => e.ResourceVersion).Order().ToList();
            Assert.Equal(notifyHistoricalVersions ? [-1, 1, 2] : [1, 2], versions);
        }

        [Fact]
        public async Task GivenAMatchingChange_WhenPumped_ThenProgressAndEventMetricsAreRecorded()
        {
            // Arrange
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            await ActivateSubscriptionAsync("s1");
            await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();

            // Act
            await pump.PumpAsync(CancellationToken.None);

            // Assert
            Metrics.Received().PumpProgress(Arg.Is<int>(n => n >= 1), Arg.Any<TimeSpan>());
            Metrics.Received().EventsCreated(1, 0);
        }

        [Fact]
        public async Task GivenTheChangeFeedSourceWithoutChangeCapture_WhenPumped_ThenItDoesNotRunOrCreateACursor()
        {
            // Arrange
            Configuration.EventSource = SubscriptionEventSource.ResourceChangeData;
            SubscriptionEventWatchdog pump = CreatePump("true");

            // Act
            int processed = await pump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.Equal(0, processed);
            Assert.Null(await Store.GetCursorAsync(ResourceChangeDataEventSource.ChangeFeedCursorId, CancellationToken.None));
        }
    }
}
