// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Compartment;
using Microsoft.Health.Fhir.Core.Features.FhirPath;
using Microsoft.Health.Fhir.Core.Features.Operations.Import;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.Core.Messages.Delete;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Extensions;
using Microsoft.Health.Fhir.SqlServer.Features.ChangeFeed;
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
    /// Shared setup for the SQL Server subscription store and event pump tests (ADR-2611). It has no tests of its own.
    /// </summary>
    public abstract class SqlServerSubscriptionTestBase : IAsyncLifetime
    {
        protected const string TopicUrl = "http://example.org/topic/encounter-cancelled";

        protected SqlServerSubscriptionTestBase(FhirStorageTestsFixture fixture, ITestOutputHelper output)
        {
            Output = output;
            Fixture = fixture;
            Sql = (SqlServerFhirStorageTestsFixture)fixture.Service;
            Store = new SqlServerSubscriptionStore(Sql.SqlRetryService, Sql.SqlServerFhirModel, Sql.SchemaInformation, NullLogger<SqlServerSubscriptionStore>.Instance);
        }

        protected FhirStorageTestsFixture Fixture { get; }

        protected SqlServerFhirStorageTestsFixture Sql { get; }

        private protected SqlServerSubscriptionStore Store { get; }

        protected SubscriptionsConfiguration Configuration { get; } = new() { Enabled = true, TransactionReadLagSeconds = 0 };

        protected ISubscriptionMetricHandler Metrics { get; } = Substitute.For<ISubscriptionMetricHandler>();

        protected ITestOutputHelper Output { get; }

        /// <summary>
        /// Gets a value indicating whether the database records the <c>ResourceChangeData</c> change feed.
        /// </summary>
        protected virtual bool ChangeCaptureEnabled => false;

        private protected StubConverter Converter { get; } = new();

        private protected StubLoader Loader { get; } = new();

        /// <summary>
        /// Gets or sets a decorator applied to each event source of pumps created afterwards, for example to inject failures.
        /// </summary>
        private protected Func<ITransactionEventSource, ITransactionEventSource> WrapSource { get; set; }

        public ValueTask InitializeAsync()
        {
            ExecuteSql("DELETE FROM dbo.SubscriptionEvents; DELETE FROM dbo.SubscriptionStates; DELETE FROM dbo.Parameters WHERE Id IN ('" +
                SqlServerSubscriptionStore.CursorId + "', '" + ResourceChangeDataEventSource.ChangeFeedCursorId + "', '" + SqlServerSubscriptionStore.EventSourceId + "')");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private protected SubscriptionEventWatchdog CreatePump(string fhirPathCriteria, params SubscriptionInteraction[] interactions) => CreatePump("Observation", fhirPathCriteria, interactions);

        private protected SubscriptionEventWatchdog CreatePump(string triggerResource, string fhirPathCriteria, SubscriptionInteraction[] interactions)
        {
            Converter.Topic = new SubscriptionTopicInfo
            {
                ResourceId = "t1",
                Canonical = TopicUrl,
                Status = "active",
                ResourceTriggers = [new TopicResourceTrigger { Resource = triggerResource, FhirPathCriteria = fhirPathCriteria, SupportedInteractions = interactions }],
            };
            Converter.Subscription = new SubscriptionInfo
            {
                Id = "s1",
                VersionId = "1",
                Status = SubscriptionStatusCode.Active,
                TopicCanonical = TopicUrl,
                ChannelType = "rest-hook",
                Endpoint = "https://hooks.example.com/notify",
                ContentType = "application/fhir+json",
                End = DateTimeOffset.UtcNow.AddDays(7),
            };

            IOptions<SubscriptionsConfiguration> options = Options.Create(Configuration);
            var fhirPath = new FhirPathCriteriaEvaluator(new FirelyFhirPathProvider());
            var searchEvaluator = new InMemorySearchEvaluator(Substitute.For<IExpressionParser>(), Substitute.For<ISearchIndexer>(), Substitute.For<ICompartmentIndexer>());
            var channel = Substitute.For<ISubscriptionChannel>();
            channel.ChannelType.Returns("rest-hook");
            var validator = new SubscriptionValidator(searchEvaluator, fhirPath, new SubscriptionEndpointPolicy(options), options, ModelInfoProvider.Instance, [channel]);
            var registry = new SubscriptionRegistry(Loader, Converter, Store, validator, NullLogger<SubscriptionRegistry>.Instance);
            var authorizer = new SubscriptionAuthorizer(Options.Create(new SecurityConfiguration()), searchEvaluator);
            var processor = new SubscriptionEventProcessor(new SubscriptionMatcher(searchEvaluator, fhirPath), authorizer, XUnitLogger<SubscriptionEventProcessor>.Create(Output), options);

            var reader = new SqlResourceChangeReader(
                Sql.SqlServerFhirDataStore.StoreClient,
                Sql.SqlServerFhirModel,
                new CompressedRawResourceConverter(),
                Fixture.Deserializer,
                XUnitLogger<SqlResourceChangeReader>.Create(Output));
            var changeFeed = new SqlServerFhirResourceChangeDataStore(Sql.SqlConnectionWrapperFactory, NullLogger<SqlServerFhirResourceChangeDataStore>.Instance, Sql.SchemaInformation);
            ITransactionEventSource[] sources =
            [
                new TransactionsEventSource(reader, options, XUnitLogger<TransactionsEventSource>.Create(Output), Metrics),
                new ResourceChangeDataEventSource(changeFeed, reader, Options.Create(new CoreFeatureConfiguration { SupportsResourceChangeCapture = ChangeCaptureEnabled })),
            ];

            return new SubscriptionEventWatchdog(
                WrapSource == null ? sources : sources.Select(WrapSource).ToArray(),
                Store,
                registry,
                processor,
                Fixture.FhirRequestContextAccessor,
                Sql.SchemaInformation,
                options,
                Sql.SqlRetryService,
                XUnitLogger<SubscriptionEventWatchdog>.Create(Output),
                Metrics);
        }

        private protected TransactionsEventSource CreateTransactionsSource()
        {
            var reader = new SqlResourceChangeReader(
                Sql.SqlServerFhirDataStore.StoreClient,
                Sql.SqlServerFhirModel,
                new CompressedRawResourceConverter(),
                Fixture.Deserializer,
                XUnitLogger<SqlResourceChangeReader>.Create(Output));
            return new TransactionsEventSource(reader, Options.Create(Configuration), XUnitLogger<TransactionsEventSource>.Create(Output), Metrics) { LateCheckPeriod = TimeSpan.Zero };
        }

        protected async Task ActivateSubscriptionAsync(string id)
        {
            await Store.SaveGrantAsync(id, Converter.Subscription.ComputeContentHash(), new SubscriptionGrant { ContentHash = Converter.Subscription.ComputeContentHash() }.Serialize(), CancellationToken.None);
            await AdvanceVisibilityAsync();
            await Store.ActivateAsync(id, CancellationToken.None);
        }

        protected async Task<string> CreateObservationAsync(string status) => await CreateAsync("Observation", Guid.NewGuid().ToString(), status);

        protected async Task UpdateObservationAsync(string id, string status) => await CreateAsync("Observation", id, status);

        protected async Task<string> CreateAsync(string type, string id, string status)
        {
            string json = type == "Observation"
                ? $"{{\"resourceType\":\"Observation\",\"id\":\"{id}\",\"status\":\"{status}\",\"code\":{{\"text\":\"test\"}}}}"
                : $"{{\"resourceType\":\"{type}\",\"id\":\"{id}\",\"name\":\"{status}\"}}";
            await Fixture.Mediator.UpsertResourceAsync(new Hl7.Fhir.Serialization.FhirJsonParser().Parse<Resource>(json).ToResourceElement());
            return id;
        }

        /// <summary>
        /// Imports one version of a patient the way <c>$import</c> incremental mode does, keeping its <c>lastUpdated</c>.
        /// Importing a version older than the current one stores it directly as history.
        /// </summary>
        /// <param name="id">The patient id.</param>
        /// <param name="lastUpdated">The version's lastUpdated.</param>
        /// <returns>A task.</returns>
        protected async Task ImportPatientAsync(string id, DateTimeOffset lastUpdated)
        {
            var patient = new Patient { Id = id, Meta = new Meta { LastUpdated = lastUpdated } };
            ResourceElement resource = patient.ToTypedElement().ToResourceElement();
            var rawResourceFactory = new RawResourceFactory(new Hl7.Fhir.Serialization.FhirJsonSerializer());
            var wrapper = new ResourceWrapper(resource, rawResourceFactory.Create(resource, keepMeta: true), new ResourceRequest("Import"), deleted: false, new List<SearchIndexEntry>(), null, null, "ABC");
            await ((SqlServerFhirDataStore)Fixture.DataStore).ImportResourcesAsync([new ImportResource(0, 0, 0, true, false, false, wrapper)], ImportMode.IncrementalLoad, true, false, CancellationToken.None);
        }

        protected async Task AdvanceVisibilityAsync()
        {
            await Sql.SqlServerFhirDataStore.StoreClient.MergeResourcesAdvanceTransactionVisibilityAsync(CancellationToken.None);
        }

        protected static SubscriptionEventCandidate Candidate(string subscriptionId, long transactionId, long surrogateId, SubscriptionEventStatus status = SubscriptionEventStatus.Pending)
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

        protected void ExecuteSql(string sql)
        {
            using var conn = new SqlConnection(Sql.TestConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            cmd.ExecuteNonQuery();
        }

        protected string Dump(string sql)
        {
            using var conn = new SqlConnection(Sql.TestConnectionString);
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

        protected T ExecuteScalar<T>(string sql)
        {
            using var conn = new SqlConnection(Sql.TestConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(sql, conn);
            return (T)cmd.ExecuteScalar();
        }

        private protected sealed class StubConverter : ISubscriptionModelConverter
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

        private protected sealed class StubLoader : ISubscriptionResourceLoader
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
