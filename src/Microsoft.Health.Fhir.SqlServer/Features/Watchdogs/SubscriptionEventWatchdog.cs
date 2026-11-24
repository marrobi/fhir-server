// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Subscriptions;
using Microsoft.Health.SqlServer.Features.Schema;

namespace Microsoft.Health.Fhir.SqlServer.Features.Watchdogs
{
    /// <summary>
    /// The subscription event pump (ADR-2611). A leased singleton pages committed transactions from its cursor up to the
    /// visibility watermark, reads every resource version each transaction wrote (including versions that are already history),
    /// matches them to subscriptions, and commits the resulting outbox events together with the cursor. A crash before the commit
    /// replays the batch; replays are idempotent, so no event is lost or numbered twice.
    /// </summary>
    internal sealed class SubscriptionEventWatchdog : Watchdog<SubscriptionEventWatchdog>
    {
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(30);

        private readonly SqlStoreClient _store;
        private readonly SqlServerSubscriptionStore _subscriptionStore;
        private readonly SubscriptionRegistry _registry;
        private readonly SubscriptionEventProcessor _processor;
        private readonly ISqlServerFhirModel _model;
        private readonly ICompressedRawResourceConverter _rawResourceConverter;
        private readonly IResourceDeserializer _deserializer;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;
        private readonly SchemaInformation _schemaInformation;
        private readonly SubscriptionsConfiguration _configuration;
        private readonly ILogger<SubscriptionEventWatchdog> _logger;
        private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;

        public SubscriptionEventWatchdog(
            SqlStoreClient store,
            SqlServerSubscriptionStore subscriptionStore,
            SubscriptionRegistry registry,
            SubscriptionEventProcessor processor,
            ISqlServerFhirModel model,
            ICompressedRawResourceConverter rawResourceConverter,
            IResourceDeserializer deserializer,
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor,
            SchemaInformation schemaInformation,
            IOptions<SubscriptionsConfiguration> configuration,
            ISqlRetryService sqlRetryService,
            ILogger<SubscriptionEventWatchdog> logger)
            : base(sqlRetryService, logger)
        {
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _subscriptionStore = EnsureArg.IsNotNull(subscriptionStore, nameof(subscriptionStore));
            _registry = EnsureArg.IsNotNull(registry, nameof(registry));
            _processor = EnsureArg.IsNotNull(processor, nameof(processor));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _rawResourceConverter = EnsureArg.IsNotNull(rawResourceConverter, nameof(rawResourceConverter));
            _deserializer = EnsureArg.IsNotNull(deserializer, nameof(deserializer));
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
            _schemaInformation = EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            PeriodSec = Math.Max(0.1, _configuration.PumpPeriodSeconds);
        }

        internal SubscriptionEventWatchdog()
        {
            // this is used to get param names for testing
        }

        public override double LeasePeriodSec { get; internal set; } = 20;

        public override bool AllowRebalance { get; internal set; } = false;

        public override double PeriodSec { get; internal set; } = 1;

        protected override async Task RunWorkAsync(CancellationToken cancellationToken)
        {
            if (!_configuration.Enabled || _schemaInformation.Current < SchemaVersionConstants.Subscriptions)
            {
                return;
            }

            await PumpAsync(cancellationToken);
        }

        /// <summary>
        /// Processes all visible transactions after the cursor, within a time budget.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of transactions processed.</returns>
        internal async Task<int> PumpAsync(CancellationToken cancellationToken)
        {
            SetBackgroundRequestContext();

            long visibility = await _store.MergeResourcesGetTransactionVisibilityAsync(cancellationToken);
            long? cursor = await _subscriptionStore.GetCursorAsync(cancellationToken);
            if (cursor == null)
            {
                // First run: start from now. Subscriptions never receive changes from before their activation anyway.
                await _subscriptionStore.EnqueueAsync([], visibility, cancellationToken);
                _logger.LogInformation("Subscription event pump initialized at transaction {TransactionId}.", visibility);
                return 0;
            }

            await CleanupIfDueAsync(cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            int processed = 0;
            while (cursor < visibility && stopwatch.Elapsed < MaxRunTime && !cancellationToken.IsCancellationRequested)
            {
                // GetTransactions returns at most 10,000 transactions, so page until the watermark is reached.
                IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> transactions =
                    await _store.GetTransactionsAsync(cursor.Value, visibility, cancellationToken);
                if (transactions.Count == 0)
                {
                    break;
                }

                IReadOnlyList<SubscriptionState> states = await _subscriptionStore.GetStatesAsync(cancellationToken);
                SubscriptionRegistrySnapshot snapshot = await _registry.EnsureIncludesAsync(states, cancellationToken);
                List<RegisteredSubscription> matchable = snapshot.Subscriptions.Where(s => s.IsMatchable).ToList();

                foreach (long[] batch in transactions.Select(t => t.TransactionId).OrderBy(t => t).Chunk(Math.Max(1, _configuration.PumpTransactionBatchSize)))
                {
                    IReadOnlyList<ResourceChange> changes = matchable.Count == 0 ? [] : await ReadChangesAsync(batch, matchable, cancellationToken);
                    IReadOnlyList<SubscriptionEventCandidate> events = _processor.Process(changes, matchable);
                    await _subscriptionStore.EnqueueAsync(events, batch[^1], cancellationToken);
                    cursor = batch[^1];
                    processed += batch.Length;
                }
            }

            if (processed > 0)
            {
                _logger.LogInformation("Subscription event pump processed {Count} transactions up to {TransactionId}.", processed, cursor);
            }

            return processed;
        }

        private async Task<IReadOnlyList<ResourceChange>> ReadChangesAsync(long[] transactionIds, IReadOnlyList<RegisteredSubscription> matchable, CancellationToken cancellationToken)
        {
            var changes = new List<ResourceChange>();
            foreach (long transactionId in transactionIds)
            {
                IReadOnlyList<ResourceWrapper> wrappers = await _store.GetAllResourceVersionsByTransactionIdAsync(transactionId, _rawResourceConverter.ReadCompressedRawResource, _model.GetResourceTypeName, cancellationToken);
                foreach (ResourceWrapper wrapper in wrappers.OrderBy(w => w.ResourceSurrogateId))
                {
                    if (!SubscriptionEventProcessor.IsCovered(matchable, wrapper.ResourceTypeName))
                    {
                        continue;
                    }

                    changes.Add(new ResourceChange
                    {
                        TransactionId = transactionId,
                        ResourceTypeId = _model.GetResourceTypeId(wrapper.ResourceTypeName),
                        ResourceSurrogateId = wrapper.ResourceSurrogateId,
                        ResourceType = wrapper.ResourceTypeName,
                        ResourceId = wrapper.ResourceId,
                        Version = int.Parse(wrapper.Version, CultureInfo.InvariantCulture),
                        IsDeleted = wrapper.IsDeleted,
                        LastModified = wrapper.LastModified,
                        Current = ToSnapshot(wrapper),
                    });
                }
            }

            await LoadPreviousVersionsAsync(changes, cancellationToken);
            return changes;
        }

        private async Task LoadPreviousVersionsAsync(List<ResourceChange> changes, CancellationToken cancellationToken)
        {
            List<ResourceChange> needPrevious = changes.Where(c => c.HasPreviousVersion).ToList();
            if (needPrevious.Count == 0)
            {
                return;
            }

            List<ResourceDateKey> keys = needPrevious
                .Select(c => new ResourceDateKey(c.ResourceTypeId, c.ResourceId, 0, (c.Version - 1).ToString(CultureInfo.InvariantCulture)))
                .DistinctBy(k => (k.ResourceTypeId, k.Id, k.VersionId))
                .ToList();

            IReadOnlyList<ResourceWrapper> previous = await _store.GetAsync(keys, _rawResourceConverter.ReadCompressedRawResource, _model.GetResourceTypeName, isReadOnly: false, cancellationToken, includeInvisible: true);
            Dictionary<(string, string, string), ResourceWrapper> byKey = previous.GroupBy(w => (w.ResourceTypeName, w.ResourceId, w.Version)).ToDictionary(g => g.Key, g => g.First());

            foreach (ResourceChange change in needPrevious)
            {
                change.PreviousLoaded = true;
                if (byKey.TryGetValue((change.ResourceType, change.ResourceId, (change.Version - 1).ToString(CultureInfo.InvariantCulture)), out ResourceWrapper wrapper))
                {
                    change.PreviousIsDeleted = wrapper.IsDeleted;
                    change.Previous = ToSnapshot(wrapper);
                }
            }
        }

        private ResourceSnapshot ToSnapshot(ResourceWrapper wrapper)
        {
            if (SqlStoreClient.IsInvisible(wrapper))
            {
                return null;
            }

            try
            {
                return new ResourceSnapshot(_deserializer.Deserialize(wrapper));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Subscription event pump could not read {ResourceType}/{ResourceId}/_history/{Version}.", wrapper.ResourceTypeName, wrapper.ResourceId, wrapper.Version);
                return null;
            }
        }

        private async Task CleanupIfDueAsync(CancellationToken cancellationToken)
        {
            if (Clock.UtcNow - _lastCleanup < CleanupInterval)
            {
                return;
            }

            _lastCleanup = Clock.UtcNow;
            await _subscriptionStore.CleanupEventsAsync(Math.Max(1, _configuration.EventRetentionDays), cancellationToken);
        }

        private void SetBackgroundRequestContext()
        {
            _requestContextAccessor.RequestContext = new FhirRequestContext(
                method: Name,
                uriString: "https://localhost/",
                baseUriString: "https://localhost/",
                correlationId: Guid.NewGuid().ToString(),
                requestHeaders: new Dictionary<string, StringValues>(),
                responseHeaders: new Dictionary<string, StringValues>())
            {
                IsBackgroundTask = true,
            };
        }
    }
}
