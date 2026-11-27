// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Subscriptions;
using Microsoft.Health.SqlServer.Features.Schema;

namespace Microsoft.Health.Fhir.SqlServer.Features.Watchdogs
{
    /// <summary>
    /// The subscription event pump (ADR-2611). A leased singleton reads committed changes from the configured
    /// <see cref="ITransactionEventSource"/> after its cursor, matches the exact versions written to subscriptions, and commits the
    /// resulting outbox events together with the cursor. A crash before the commit replays the page; replays are idempotent, so no
    /// event is lost or numbered twice.
    /// </summary>
    internal sealed class SubscriptionEventWatchdog : Watchdog<SubscriptionEventWatchdog>
    {
        private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

        // Cleanup runs inside the pump, so it is time-boxed; what is left is removed on the next pump run.
        private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MaxRunTime = TimeSpan.FromSeconds(30);

        private readonly IReadOnlyList<ITransactionEventSource> _sources;
        private readonly SqlServerSubscriptionStore _subscriptionStore;
        private readonly SubscriptionRegistry _registry;
        private readonly SubscriptionEventProcessor _processor;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;
        private readonly SchemaInformation _schemaInformation;
        private readonly SubscriptionsConfiguration _configuration;
        private readonly ISubscriptionMetricHandler _metrics;
        private readonly IFhirServerInstanceConfiguration _instanceConfiguration;
        private readonly ILogger<SubscriptionEventWatchdog> _logger;
        private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
        private string _reportedUnavailable;

        public SubscriptionEventWatchdog(
            IEnumerable<ITransactionEventSource> sources,
            SqlServerSubscriptionStore subscriptionStore,
            SubscriptionRegistry registry,
            SubscriptionEventProcessor processor,
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor,
            SchemaInformation schemaInformation,
            IOptions<SubscriptionsConfiguration> configuration,
            ISqlRetryService sqlRetryService,
            ILogger<SubscriptionEventWatchdog> logger,
            ISubscriptionMetricHandler metrics = null,
            IFhirServerInstanceConfiguration instanceConfiguration = null)
            : base(sqlRetryService, logger)
        {
            _sources = EnsureArg.IsNotNull(sources, nameof(sources)).ToList();
            _subscriptionStore = EnsureArg.IsNotNull(subscriptionStore, nameof(subscriptionStore));
            _registry = EnsureArg.IsNotNull(registry, nameof(registry));
            _processor = EnsureArg.IsNotNull(processor, nameof(processor));
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
            _schemaInformation = EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _metrics = metrics;
            _instanceConfiguration = instanceConfiguration;
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
        /// Processes committed changes after the cursor of the configured event source, within a time budget.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The number of source positions processed.</returns>
        internal async Task<int> PumpAsync(CancellationToken cancellationToken)
        {
            SetBackgroundRequestContext();

            ITransactionEventSource source = GetSource();
            if (source == null)
            {
                return 0;
            }

            long? cursor = await StartSourceAsync(source, cancellationToken);
            if (cursor == null)
            {
                return 0;
            }

            await CleanupIfDueAsync(cancellationToken);

            var stopwatch = Stopwatch.StartNew();
            int processed = 0;
            while (stopwatch.Elapsed < MaxRunTime && !cancellationToken.IsCancellationRequested)
            {
                // Read the activation counter before the states: if a subscription is activated while this page is matched,
                // the enqueue reports it and the page is matched again, so no change after an activation is missed.
                long activationVersion = await _subscriptionStore.GetActivationVersionAsync(cancellationToken);
                IReadOnlyList<SubscriptionState> states = await _subscriptionStore.GetStatesAsync(cancellationToken);
                SubscriptionRegistrySnapshot snapshot = await _registry.EnsureIncludesAsync(states, cancellationToken);
                List<RegisteredSubscription> matchable = snapshot.Subscriptions.Where(s => s.IsMatchable).ToList();
                Func<string, bool> isCovered = matchable.Count == 0 ? null : SubscriptionEventProcessor.CreateCoverage(matchable);

                TransactionEventPage page = await source.ReadAsync(cursor.Value, Math.Max(1, _configuration.PumpTransactionBatchSize), isCovered, cancellationToken);
                if (page.PositionCount == 0)
                {
                    break;
                }

                IReadOnlyList<SubscriptionEventCandidate> events = _processor.Process(page.Changes, matchable);
                if (!await _subscriptionStore.EnqueueAsync(events, source.CursorId, page.LastPosition, activationVersion, cancellationToken))
                {
                    _logger.LogInformation("Subscription event pump is matching changes after {Position} again because a subscription was activated.", cursor);
                    continue;
                }

                cursor = page.LastPosition;
                processed += page.PositionCount;

                _metrics?.PumpProgress(page.PositionCount, page.LastPositionTime.HasValue ? Clock.UtcNow - page.LastPositionTime.Value : TimeSpan.Zero);
                _metrics?.EventsCreated(events.Count(e => e.Status != SubscriptionEventStatus.Unevaluable), events.Count(e => e.Status == SubscriptionEventStatus.Unevaluable));
            }

            if (processed > 0)
            {
                _logger.LogInformation("Subscription event pump processed {Count} {Source} positions up to {Position}.", processed, source.Kind, cursor);
            }

            return processed;
        }

        /// <summary>
        /// Returns the source's cursor, positioning it first when the pump has never run. The source is fixed when the pump first runs:
        /// if a different source is configured later, the pump stops and reports it, because positions of different sources are not
        /// comparable.
        /// </summary>
        /// <param name="source">The configured source.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cursor, or null when the pump must not read now.</returns>
        private async Task<long?> StartSourceAsync(ITransactionEventSource source, CancellationToken cancellationToken)
        {
            string recorded = await _subscriptionStore.GetEventSourceAsync(cancellationToken);
            string current = source.Kind.ToString();
            if (recorded != null && !string.Equals(recorded, current, StringComparison.Ordinal))
            {
                ReportUnavailable($"The subscription event source is configured as {current}, but this database was started with {recorded}. Changing the event source of an existing database is not supported; set FhirServer:Subscriptions:EventSource back to {recorded}.");
                return null;
            }

            long? cursor = await _subscriptionStore.GetCursorAsync(source.CursorId, cancellationToken);
            if (recorded != null && cursor != null)
            {
                return cursor;
            }

            // First run: start from now, but never past an existing activation, whose subscription is owed every later change.
            IReadOnlyList<SubscriptionState> states = await _subscriptionStore.GetStatesAsync(cancellationToken);
            long? earliestActivation = states.Where(s => s.ActivationTransactionId != null).Select(s => s.ActivationTransactionId).Min();
            long start = await source.GetStartPositionAsync(earliestActivation, cancellationToken);

            await _subscriptionStore.SetEventSourceAsync(current, source.CursorId, start, cancellationToken);
            _logger.LogInformation("Subscription event pump started the {Source} source at position {Position}.", source.Kind, start);
            return null;
        }

        private ITransactionEventSource GetSource()
        {
            ITransactionEventSource source = _sources.FirstOrDefault(s => s.Kind == _configuration.EventSource);
            string reason = source == null
                ? $"No subscription event source is registered for {_configuration.EventSource}."
                : source.GetUnavailableReason();

            if (reason == null)
            {
                return source;
            }

            ReportUnavailable(reason);
            return null;
        }

        private void ReportUnavailable(string reason)
        {
            if (!string.Equals(reason, _reportedUnavailable, StringComparison.Ordinal))
            {
                _logger.LogError("Subscription event pump is not running: {Reason}", reason);
                _reportedUnavailable = reason;
            }
        }

        private async Task CleanupIfDueAsync(CancellationToken cancellationToken)
        {
            if (Clock.UtcNow - _lastCleanup < CleanupInterval)
            {
                return;
            }

            // A backlog larger than one budget continues on the next pump run, so cleanup keeps up with the rate events complete.
            if (await _subscriptionStore.CleanupEventsAsync(Math.Max(1, _configuration.EventRetentionDays), CleanupBudget, cancellationToken))
            {
                _lastCleanup = Clock.UtcNow;
            }
        }

        private void SetBackgroundRequestContext()
        {
            _requestContextAccessor.RequestContext = SubscriptionBackgroundContext.Create(Name, _instanceConfiguration?.BaseUri, Guid.NewGuid().ToString());
        }
    }
}
