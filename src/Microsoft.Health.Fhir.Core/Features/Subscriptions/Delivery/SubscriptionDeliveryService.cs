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
using Medino;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.Core.Messages.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Delivery
{
    /// <summary>
    /// Runs the subscription lifecycle (handshake, activation, error and off transitions, <c>end</c>) and delivers notifications.
    /// Every instance runs it; a per-subscription lease in the store makes all work on one subscription single-flight, so its
    /// notifications are attempted in event-number order.
    /// Handshakes, heartbeats and notifications are retried with back-off before the subscription is set to <c>error</c>. In
    /// <c>error</c>, recovery handshakes continue with back-off up to <see cref="SubscriptionsConfiguration.MaxRetryDelaySeconds"/>;
    /// the first that succeeds makes the subscription <c>active</c> again and delivery resumes from the oldest pending event.
    /// Failed recovery handshakes do not write the subscription resource, so they do not restart the <c>MaxErrorHours</c> clock.
    /// </summary>
    public sealed class SubscriptionDeliveryService : BackgroundService, INotificationHandler<SearchParametersInitializedNotification>
    {
        private const int MaxNotificationsPerLease = 50;

        // Added to a send's timeout when renewing the lease, so the lease outlives the send and the status writes after it.
        private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

        // An API write commits the subscription before its grant; within this period a missing grant is not yet treated as unauthorized.
        private static readonly TimeSpan GrantGracePeriod = TimeSpan.FromMinutes(1);

        private static readonly TimeSpan HealthReportInterval = TimeSpan.FromMinutes(1);

        private readonly SubscriptionsConfiguration _configuration;
        private readonly SubscriptionRegistry _registry;
        private readonly ISubscriptionStore _store;
        private readonly ISubscriptionResourceLoader _loader;
        private readonly ISubscriptionModelConverter _converter;
        private readonly ISubscriptionNotificationFactory _notificationFactory;
        private readonly ISubscriptionStatusWriter _statusWriter;
        private readonly SubscriptionAuthorizer _authorizer;
        private readonly Dictionary<string, ISubscriptionChannel> _channels;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;
        private readonly ILogger<SubscriptionDeliveryService> _logger;
        private readonly ISubscriptionMetricHandler _metrics;
        private readonly IFhirServerInstanceConfiguration _instanceConfiguration;
        private readonly string _worker = $"{Environment.MachineName}.{Environment.ProcessId}.{Guid.NewGuid():N}";
        private volatile bool _storageReady;
        private DateTimeOffset _nextHealthReportUtc = DateTimeOffset.MinValue;

        public SubscriptionDeliveryService(
            IOptions<SubscriptionsConfiguration> configuration,
            SubscriptionRegistry registry,
            ISubscriptionStore store,
            ISubscriptionResourceLoader loader,
            ISubscriptionModelConverter converter,
            ISubscriptionNotificationFactory notificationFactory,
            ISubscriptionStatusWriter statusWriter,
            SubscriptionAuthorizer authorizer,
            IEnumerable<ISubscriptionChannel> channels,
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor,
            ILogger<SubscriptionDeliveryService> logger,
            ISubscriptionMetricHandler metrics = null,
            IFhirServerInstanceConfiguration instanceConfiguration = null)
        {
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _registry = EnsureArg.IsNotNull(registry, nameof(registry));
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _loader = EnsureArg.IsNotNull(loader, nameof(loader));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _notificationFactory = EnsureArg.IsNotNull(notificationFactory, nameof(notificationFactory));
            _statusWriter = EnsureArg.IsNotNull(statusWriter, nameof(statusWriter));
            _authorizer = EnsureArg.IsNotNull(authorizer, nameof(authorizer));
            _channels = EnsureArg.IsNotNull(channels, nameof(channels)).ToDictionary(c => c.ChannelType, StringComparer.Ordinal);
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _metrics = metrics;
            _instanceConfiguration = instanceConfiguration;
        }

        public Task HandleAsync(SearchParametersInitializedNotification notification, CancellationToken cancellationToken)
        {
            _storageReady = true;
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.Enabled || !_converter.IsSupported)
            {
                return;
            }

            while (!_storageReady)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }

            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(0.1, _configuration.DeliveryPollingIntervalSeconds));
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Subscription delivery cycle failed.");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }

        /// <summary>
        /// Performs one lifecycle and delivery pass over all subscriptions.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            if (_store.Availability != SubscriptionStoreAvailability.Available)
            {
                // For example, while the database schema is upgraded to the version with subscription storage.
                return;
            }

            SetBackgroundRequestContext();

            SubscriptionRegistrySnapshot snapshot = await _registry.GetAsync(TimeSpan.FromSeconds(_configuration.RegistryRefreshIntervalSeconds), cancellationToken);
            Dictionary<string, SubscriptionState> states = (await _store.GetStatesAsync(cancellationToken)).ToDictionary(s => s.SubscriptionId, StringComparer.Ordinal);
            var withDueEvents = new HashSet<string>(await _store.GetSubscriptionsWithDueEventsAsync(cancellationToken), StringComparer.Ordinal);
            await ReportHealthAsync(snapshot, cancellationToken);

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (RegisteredSubscription subscription in snapshot.Subscriptions)
            {
                if (NeedsWork(subscription, states.GetValueOrDefault(subscription.Id), withDueEvents.Contains(subscription.Id)))
                {
                    candidates.Add(subscription.Id);
                }
            }

            // States without a subscription resource belong to deleted subscriptions.
            foreach (string id in states.Keys.Where(id => !snapshot.ById.ContainsKey(id)))
            {
                candidates.Add(id);
            }

            foreach (string id in candidates.OrderBy(id => id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessSubscriptionAsync(id, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Processing subscription {SubscriptionId} failed.", id);
                }
            }
        }

        private bool NeedsWork(RegisteredSubscription subscription, SubscriptionState state, bool hasDueEvents)
        {
            if (subscription.Info == null)
            {
                // An unparseable subscription is set to error once, and deactivated if needed.
                return !SubscriptionStatusCodeExtensions.ParseSubscriptionStatus(subscription.RawStatus).IsErrorOrStopped() || state?.ActivationTransactionId != null;
            }

            SubscriptionStatusCode status = subscription.Info.Status;
            if (status is SubscriptionStatusCode.Off or SubscriptionStatusCode.EnteredInError)
            {
                return state?.ActivationTransactionId != null || state?.Grant != null;
            }

            if (!subscription.IsValid)
            {
                return status != SubscriptionStatusCode.Error || state?.ActivationTransactionId != null;
            }

            if (!subscription.HasValidGrant)
            {
                // Status becomes off, so this stops once written.
                return true;
            }

            if (subscription.Info.End.HasValue && subscription.Info.End.Value <= Clock.UtcNow)
            {
                return true;
            }

            return status switch
            {
                SubscriptionStatusCode.Requested => IsContactDue(state),
                SubscriptionStatusCode.Active => hasDueEvents || state?.ActivationTransactionId == null || (IsHeartbeatDue(subscription.Info, state) && IsContactDue(state)),
                SubscriptionStatusCode.Error => IsErrorExpired(subscription) || IsContactDue(state),
                _ => false,
            };
        }

        private bool IsErrorExpired(RegisteredSubscription subscription) =>
            subscription.Info?.Status == SubscriptionStatusCode.Error &&
            _configuration.MaxErrorHours > 0 &&
            subscription.LastUpdated is DateTimeOffset written &&
            Clock.UtcNow - written >= TimeSpan.FromHours(_configuration.MaxErrorHours);

        private static bool IsHeartbeatDue(SubscriptionInfo info, SubscriptionState state)
        {
            if (info.HeartbeatPeriodSeconds is not int period || period <= 0)
            {
                return false;
            }

            DateTimeOffset last = state?.LastNotificationUtc ?? DateTimeOffset.MinValue;
            return Clock.UtcNow - last >= TimeSpan.FromSeconds(period);
        }

        private static bool IsContactDue(SubscriptionState state) => state?.NextContactUtc is not DateTimeOffset next || next <= Clock.UtcNow;

        private TimeSpan RetryDelay(int failures) =>
            TimeSpan.FromSeconds(Math.Min(_configuration.RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, failures - 1)), _configuration.MaxRetryDelaySeconds));

        /// <summary>
        /// Records a failed handshake, heartbeat or recovery check and schedules the next contact. Returns the consecutive failure count.
        /// </summary>
        private async Task<int> RecordContactFailureAsync(string subscriptionId, SubscriptionState state, string error, CancellationToken cancellationToken, int minimumFailures = 0)
        {
            int failures = Math.Max((state?.ContactFailures ?? 0) + 1, minimumFailures);
            await _store.RecordContactFailureAsync(subscriptionId, failures, Clock.UtcNow + RetryDelay(failures), error, cancellationToken);
            return failures;
        }

        private async Task ProcessSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            if (!await _store.TryAcquireLeaseAsync(subscriptionId, _worker, TimeSpan.FromSeconds(_configuration.DeliveryLeaseSeconds), cancellationToken))
            {
                return;
            }

            try
            {
                // Re-read under the lease: the snapshot may be stale, and another instance may have just acted.
                ResourceElement resource = await _loader.GetSubscriptionAsync(subscriptionId, cancellationToken);
                SubscriptionState state = await _store.GetStateAsync(subscriptionId, cancellationToken);
                if (resource == null)
                {
                    if (state != null)
                    {
                        _logger.LogInformation("Subscription {SubscriptionId} was deleted; removing its state and events.", subscriptionId);
                        await _store.DeleteStateAsync(subscriptionId, cancellationToken);
                    }

                    return;
                }

                SubscriptionRegistrySnapshot snapshot = _registry.Current;
                RegisteredSubscription subscription = _registry.Register(resource, snapshot.Topics, state);
                if (subscription.Info != null && subscription.Topic == null)
                {
                    // The topic may have been created after the last refresh.
                    snapshot = await _registry.RefreshAsync(cancellationToken);
                    subscription = _registry.Register(resource, snapshot.Topics, state);
                }

                await ApplyLifecycleAsync(subscription, state, resource, cancellationToken);
            }
            finally
            {
                await _store.ReleaseLeaseAsync(subscriptionId, _worker, CancellationToken.None);
            }
        }

        private async Task ApplyLifecycleAsync(RegisteredSubscription subscription, SubscriptionState state, ResourceElement resource, CancellationToken cancellationToken)
        {
            SubscriptionInfo info = subscription.Info;
            if (info == null)
            {
                await DeactivateAsync(subscription.Id, state, cancellationToken);
                if (!SubscriptionStatusCodeExtensions.ParseSubscriptionStatus(subscription.RawStatus).IsErrorOrStopped())
                {
                    SubscriptionInfo minimal = new() { Id = resource.Id, VersionId = resource.VersionId, Status = SubscriptionStatusCode.Requested };
                    await SetStatusAsync(minimal, SubscriptionStatusCode.Error, string.Join(" ", subscription.Errors), cancellationToken);
                }

                return;
            }

            if (info.Status is SubscriptionStatusCode.Off or SubscriptionStatusCode.EnteredInError)
            {
                await DeactivateAsync(info.Id, state, cancellationToken, revokeGrant: true);
                return;
            }

            if (!subscription.IsValid)
            {
                await DeactivateAsync(info.Id, state, cancellationToken);
                await SetStatusAsync(info, SubscriptionStatusCode.Error, string.Join(" ", subscription.Errors), cancellationToken);
                return;
            }

            if (!subscription.HasValidGrant)
            {
                if (info.Status == SubscriptionStatusCode.Requested && resource.LastUpdated is DateTimeOffset written && Clock.UtcNow - written < GrantGracePeriod)
                {
                    // The grant of an API write is saved just after the resource; wait for it rather than turning the subscription off.
                    return;
                }

                await DeactivateAsync(info.Id, state, cancellationToken);
                await SetStatusAsync(info, SubscriptionStatusCode.Off, Core.Resources.SubscriptionNotAuthorizedForContent, cancellationToken);
                return;
            }

            if (info.End.HasValue && info.End.Value <= Clock.UtcNow)
            {
                await DeactivateAsync(info.Id, state, cancellationToken, revokeGrant: true);
                await SetStatusAsync(info, SubscriptionStatusCode.Off, Core.Resources.SubscriptionEnded, cancellationToken);
                return;
            }

            if (IsErrorExpired(subscription))
            {
                await DeactivateAsync(info.Id, state, cancellationToken, revokeGrant: true);
                await SetStatusAsync(info, SubscriptionStatusCode.Off, string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionErrorExpired, _configuration.MaxErrorHours), cancellationToken);
                return;
            }

            switch (info.Status)
            {
                case SubscriptionStatusCode.Requested when IsContactDue(state):
                case SubscriptionStatusCode.Error when IsContactDue(state):
                    // In error, this is a recovery check: it either reactivates the subscription or schedules the next check.
                    await HandshakeAsync(subscription, state, cancellationToken);
                    break;
                case SubscriptionStatusCode.Active when state?.ActivationTransactionId == null:
                    // Active without activation (for example, written as active by an import): require a handshake first.
                    await SetStatusAsync(info, SubscriptionStatusCode.Requested, null, cancellationToken);
                    break;
                case SubscriptionStatusCode.Active:
                    await DeliverAsync(subscription, state, cancellationToken);
                    break;
            }
        }

        private async Task HandshakeAsync(RegisteredSubscription subscription, SubscriptionState state, CancellationToken cancellationToken)
        {
            SubscriptionInfo info = subscription.Info;
            ChannelDeliveryResult result = await SendAsync(
                subscription,
                new SubscriptionNotificationRequest
                {
                    Subscription = info,
                    Type = SubscriptionNotificationType.Handshake,
                    Status = SubscriptionStatusCode.Requested,
                    EventsSinceSubscriptionStart = state?.LastEventNumber ?? 0,
                    BaseUri = GetBaseUri(subscription),
                },
                cancellationToken);

            if (result == null)
            {
                return;
            }

            if (!result.Succeeded)
            {
                string error = string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionHandshakeFailed, result.Error);
                int failures = await RecordContactFailureAsync(info.Id, state, error, cancellationToken);
                _logger.LogInformation("Handshake for subscription {SubscriptionId} in status {Status} failed (attempt {Attempt}): {Error}", info.Id, info.Status, failures, result.Error);

                // In error, only the back-off changes: the resource is not written, so the MaxErrorHours clock keeps running.
                if (info.Status == SubscriptionStatusCode.Requested && failures >= _configuration.MaxDeliveryAttempts)
                {
                    await SetStatusAsync(info, SubscriptionStatusCode.Error, error, cancellationToken);
                }

                return;
            }

            // Keeps an existing watermark, so events matched while in error are delivered in order after recovery.
            // A recovery handshake keeps the failure count: only a delivered event or heartbeat proves the endpoint works.
            long activation = await _store.ActivateAsync(info.Id, cancellationToken);
            int keptFailures = info.Status == SubscriptionStatusCode.Error ? state?.ContactFailures ?? 0 : 0;
            await _store.SetLastNotificationAsync(info.Id, Clock.UtcNow, keptFailures, cancellationToken);
            await SetStatusAsync(info, SubscriptionStatusCode.Active, null, cancellationToken);
            _logger.LogInformation("Subscription {SubscriptionId} is active after status {Status}; transactions after {TransactionId} are matched.", info.Id, info.Status, activation);
        }

        private async Task DeliverAsync(RegisteredSubscription subscription, SubscriptionState state, CancellationToken cancellationToken)
        {
            SubscriptionInfo info = subscription.Info;
            int batchSize = Math.Clamp(info.MaxCount ?? 1, 1, _configuration.MaxEventsPerNotification);
            bool sent = false;

            for (int i = 0; i < MaxNotificationsPerLease; i++)
            {
                IReadOnlyList<SubscriptionEventRecord> pending = await _store.GetPendingEventsAsync(info.Id, batchSize, cancellationToken);
                if (pending.Count == 0 || pending[0].NextAttemptUtc > Clock.UtcNow)
                {
                    break;
                }

                // Re-check authorization at delivery to observe revocation, for example a role removed from configuration.
                // A gap caused by revocation is acceptable.
                List<SubscriptionEventRecord> authorized = pending.Where(e => _authorizer.CanRead(subscription.Grant, e.ResourceType)).ToList();
                List<long> revoked = pending.Where(e => !authorized.Contains(e)).Select(e => e.EventNumber).ToList();
                if (revoked.Count > 0)
                {
                    await _store.SetEventStatusAsync(info.Id, revoked, SubscriptionEventStatus.NotAuthorized, cancellationToken);
                }

                if (authorized.Count == 0)
                {
                    continue;
                }

                static ResourceKey VersionKey(SubscriptionEventRecord record) =>
                    new(record.ResourceType, record.ResourceId, record.ResourceVersion.ToString(CultureInfo.InvariantCulture));

                IReadOnlyDictionary<ResourceKey, ResourceElement> contents = info.Content == SubscriptionContentLevel.FullResource
                    ? await _loader.GetResourceVersionsAsync(authorized.Where(r => r.Interaction != SubscriptionInteraction.Delete).Select(VersionKey).Distinct().ToList(), cancellationToken)
                    : null;

                var events = new List<SubscriptionNotificationEvent>(authorized.Count);
                foreach (SubscriptionEventRecord record in authorized)
                {
                    ResourceElement content = record.Interaction != SubscriptionInteraction.Delete ? contents?.GetValueOrDefault(VersionKey(record)) : null;

                    events.Add(new SubscriptionNotificationEvent
                    {
                        EventNumber = record.EventNumber,
                        Timestamp = record.EventUtc,
                        ResourceType = record.ResourceType,
                        ResourceId = record.ResourceId,
                        ResourceVersion = record.ResourceVersion,
                        Interaction = record.Interaction,
                        Resource = content,
                    });
                }

                SubscriptionState latest = await _store.GetStateAsync(info.Id, cancellationToken) ?? state;
                ChannelDeliveryResult result = await SendAsync(
                    subscription,
                    new SubscriptionNotificationRequest
                    {
                        Subscription = info,
                        Type = SubscriptionNotificationType.EventNotification,
                        Status = SubscriptionStatusCode.Active,
                        EventsSinceSubscriptionStart = latest.LastEventNumber,
                        Events = events,
                        BaseUri = GetBaseUri(subscription),
                    },
                    cancellationToken);

                if (result == null)
                {
                    return;
                }

                List<long> numbers = authorized.Select(e => e.EventNumber).ToList();
                if (result.Succeeded)
                {
                    await _store.SetEventStatusAsync(info.Id, numbers, SubscriptionEventStatus.Delivered, cancellationToken);
                    await _store.SetLastNotificationAsync(info.Id, Clock.UtcNow, 0, cancellationToken);
                    sent = true;
                    continue;
                }

                int attempts = authorized.Max(e => e.Attempts) + 1;
                _logger.LogInformation("Delivery to subscription {SubscriptionId} failed (attempt {Attempt}): {Error}", info.Id, attempts, result.Error);
                if (attempts >= _configuration.MaxDeliveryAttempts)
                {
                    // The events stay pending with their numbers, and are delivered after a recovery handshake succeeds.
                    // Recovery checks continue the back-off from this attempt count rather than starting again.
                    string error = string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionDeliveryFailed, attempts, result.Error);
                    await _store.RecordFailedAttemptAsync(info.Id, numbers, Clock.UtcNow, result.Error, cancellationToken);
                    await RecordContactFailureAsync(info.Id, latest, error, cancellationToken, minimumFailures: attempts);
                    await SetStatusAsync(info, SubscriptionStatusCode.Error, error, cancellationToken);
                    return;
                }

                await _store.RecordFailedAttemptAsync(info.Id, numbers, Clock.UtcNow + RetryDelay(attempts), result.Error, cancellationToken);
                return;
            }

            SubscriptionState current = await _store.GetStateAsync(info.Id, cancellationToken) ?? state;
            if (!sent && IsHeartbeatDue(info, current) && IsContactDue(current))
            {
                ChannelDeliveryResult heartbeat = await SendAsync(
                    subscription,
                    new SubscriptionNotificationRequest
                    {
                        Subscription = info,
                        Type = SubscriptionNotificationType.Heartbeat,
                        Status = SubscriptionStatusCode.Active,
                        EventsSinceSubscriptionStart = current.LastEventNumber,
                        BaseUri = GetBaseUri(subscription),
                    },
                    cancellationToken);

                if (heartbeat == null)
                {
                    return;
                }

                if (heartbeat.Succeeded)
                {
                    await _store.SetLastNotificationAsync(info.Id, Clock.UtcNow, 0, cancellationToken);
                }
                else
                {
                    string error = string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionHeartbeatFailed, heartbeat.Error);
                    int failures = await RecordContactFailureAsync(info.Id, current, error, cancellationToken);
                    _logger.LogInformation("Heartbeat for subscription {SubscriptionId} failed (attempt {Attempt}): {Error}", info.Id, failures, heartbeat.Error);
                    if (failures >= _configuration.MaxDeliveryAttempts)
                    {
                        await SetStatusAsync(info, SubscriptionStatusCode.Error, error, cancellationToken);
                    }
                }
            }
        }

        /// <summary>
        /// Sends a notification, first renewing the lease. Returns null when the lease was lost: the caller must stop, because
        /// another worker now owns the subscription.
        /// </summary>
        private async Task<ChannelDeliveryResult> SendAsync(RegisteredSubscription subscription, SubscriptionNotificationRequest request, CancellationToken cancellationToken)
        {
            if (!_channels.TryGetValue(subscription.Info.ChannelType, out ISubscriptionChannel channel))
            {
                return ChannelDeliveryResult.Failure(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionChannelNotSupported, subscription.Info.ChannelType));
            }

            // Renew the lease for longer than this send can take, so no other worker sends for the subscription meanwhile.
            int timeoutSeconds = subscription.Info.TimeoutSeconds ?? _configuration.DefaultTimeoutSeconds;
            TimeSpan leasePeriod = TimeSpan.FromSeconds(Math.Max(_configuration.DeliveryLeaseSeconds, timeoutSeconds + LeaseMargin.TotalSeconds));
            if (!await _store.TryAcquireLeaseAsync(subscription.Id, _worker, leasePeriod, cancellationToken))
            {
                _logger.LogInformation("Subscription {SubscriptionId}: the delivery lease was lost; another worker continues.", subscription.Id);
                return null;
            }

            string payload = _notificationFactory.Serialize(_notificationFactory.CreateNotification(request), subscription.Info.ContentType);
            long started = Stopwatch.GetTimestamp();
            ChannelDeliveryResult result = await channel.SendAsync(subscription.Info, payload, cancellationToken);
            _metrics?.NotificationSent(request.Type.ToString(), result.Succeeded, Stopwatch.GetElapsedTime(started));
            return result;
        }

        private async Task ReportHealthAsync(SubscriptionRegistrySnapshot snapshot, CancellationToken cancellationToken)
        {
            if (_metrics == null || Clock.UtcNow < _nextHealthReportUtc)
            {
                return;
            }

            _nextHealthReportUtc = Clock.UtcNow + HealthReportInterval;
            try
            {
                int inError = snapshot.Subscriptions.Count(s => (s.Info?.Status ?? SubscriptionStatusCodeExtensions.ParseSubscriptionStatus(s.RawStatus)) == SubscriptionStatusCode.Error);
                SubscriptionBacklog backlog = await _store.GetBacklogAsync(cancellationToken);
                _metrics.Health(inError, backlog.PendingEvents, backlog.OldestPendingEventUtc is DateTimeOffset oldest ? Clock.UtcNow - oldest : TimeSpan.Zero);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Monitoring must not stop delivery.
                _logger.LogWarning(ex, "Reporting subscription health failed.");
            }
        }

        private async Task DeactivateAsync(string subscriptionId, SubscriptionState state, CancellationToken cancellationToken, bool revokeGrant = false)
        {
            if (state?.ActivationTransactionId != null || (revokeGrant && state?.Grant != null))
            {
                await _store.DeactivateAsync(subscriptionId, revokeGrant, cancellationToken);
            }
        }

        private async Task SetStatusAsync(SubscriptionInfo info, SubscriptionStatusCode status, string error, CancellationToken cancellationToken)
        {
            if (info.Status == status && string.Equals(info.Error ?? string.Empty, error ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            if (await _statusWriter.SetStatusAsync(info.Id, info.VersionId, status, error, cancellationToken))
            {
                _metrics?.StatusChanged(status.ToString());
                await _store.SetErrorAsync(info.Id, error, cancellationToken);
                _registry.Invalidate();
            }
        }

        private static Uri GetBaseUri(RegisteredSubscription subscription)
        {
            return subscription.Grant?.RequestBase != null && Uri.TryCreate(subscription.Grant.RequestBase, UriKind.Absolute, out Uri uri) ? uri : null;
        }

        private void SetBackgroundRequestContext()
        {
            _requestContextAccessor.RequestContext = SubscriptionBackgroundContext.Create("SubscriptionDelivery", _instanceConfiguration?.BaseUri);
        }
    }
}
