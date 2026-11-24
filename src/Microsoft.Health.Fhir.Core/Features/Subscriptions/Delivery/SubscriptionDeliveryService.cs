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
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
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
    /// </summary>
    public sealed class SubscriptionDeliveryService : BackgroundService, INotificationHandler<SearchParametersInitializedNotification>
    {
        private const int MaxNotificationsPerLease = 50;

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
        private readonly string _worker = $"{Environment.MachineName}.{Environment.ProcessId}.{Guid.NewGuid():N}";
        private volatile bool _storageReady;

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
            ISubscriptionMetricHandler metrics = null)
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
            SetBackgroundRequestContext();

            SubscriptionRegistrySnapshot snapshot = await _registry.GetAsync(TimeSpan.FromSeconds(_configuration.RegistryRefreshIntervalSeconds), cancellationToken);
            Dictionary<string, SubscriptionState> states = (await _store.GetStatesAsync(cancellationToken)).ToDictionary(s => s.SubscriptionId, StringComparer.Ordinal);
            var withDueEvents = new HashSet<string>(await _store.GetSubscriptionsWithDueEventsAsync(cancellationToken), StringComparer.Ordinal);

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

        private static bool NeedsWork(RegisteredSubscription subscription, SubscriptionState state, bool hasDueEvents)
        {
            if (subscription.Info == null)
            {
                // An unparseable subscription is set to error once, and deactivated if needed.
                return subscription.RawStatus is not ("error" or "off" or "entered-in-error") || state?.ActivationTransactionId != null;
            }

            SubscriptionStatusCode status = subscription.Info.Status;
            if (status is SubscriptionStatusCode.Off or SubscriptionStatusCode.EnteredInError)
            {
                return state?.ActivationTransactionId != null;
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
                SubscriptionStatusCode.Requested => true,
                SubscriptionStatusCode.Active => hasDueEvents || state?.ActivationTransactionId == null || IsHeartbeatDue(subscription.Info, state),
                _ => false,
            };
        }

        private static bool IsHeartbeatDue(SubscriptionInfo info, SubscriptionState state)
        {
            if (info.HeartbeatPeriodSeconds is not int period || period <= 0)
            {
                return false;
            }

            DateTimeOffset last = state?.LastNotificationUtc ?? DateTimeOffset.MinValue;
            return Clock.UtcNow - last >= TimeSpan.FromSeconds(period);
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
                if (subscription.RawStatus is not ("error" or "off" or "entered-in-error"))
                {
                    SubscriptionInfo minimal = new() { Id = resource.Id, VersionId = resource.VersionId, Status = SubscriptionStatusCode.Requested };
                    await SetStatusAsync(minimal, SubscriptionStatusCode.Error, string.Join(" ", subscription.Errors), cancellationToken);
                }

                return;
            }

            if (info.Status is SubscriptionStatusCode.Off or SubscriptionStatusCode.EnteredInError)
            {
                await DeactivateAsync(info.Id, state, cancellationToken);
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
                await DeactivateAsync(info.Id, state, cancellationToken);
                await SetStatusAsync(info, SubscriptionStatusCode.Off, Core.Resources.SubscriptionNotAuthorizedForContent, cancellationToken);
                return;
            }

            if (info.End.HasValue && info.End.Value <= Clock.UtcNow)
            {
                await DeactivateAsync(info.Id, state, cancellationToken);
                await SetStatusAsync(info, SubscriptionStatusCode.Off, Core.Resources.SubscriptionEnded, cancellationToken);
                return;
            }

            switch (info.Status)
            {
                case SubscriptionStatusCode.Requested:
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

            if (!result.Succeeded)
            {
                _logger.LogInformation("Handshake for subscription {SubscriptionId} failed: {Error}", info.Id, result.Error);
                await SetStatusAsync(info, SubscriptionStatusCode.Error, string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionHandshakeFailed, result.Error), cancellationToken);
                return;
            }

            long activation = await _store.ActivateAsync(info.Id, cancellationToken);
            await _store.SetLastNotificationAsync(info.Id, Clock.UtcNow, cancellationToken);
            await SetStatusAsync(info, SubscriptionStatusCode.Active, null, cancellationToken);
            _logger.LogInformation("Subscription {SubscriptionId} is active; transactions after {TransactionId} are matched.", info.Id, activation);
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

                var events = new List<SubscriptionNotificationEvent>(authorized.Count);
                foreach (SubscriptionEventRecord record in authorized)
                {
                    ResourceElement content = info.Content == SubscriptionContentLevel.FullResource && record.Interaction != SubscriptionInteraction.Delete
                        ? await _loader.GetResourceVersionAsync(record.ResourceType, record.ResourceId, record.ResourceVersion, cancellationToken)
                        : null;

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

                List<long> numbers = authorized.Select(e => e.EventNumber).ToList();
                if (result.Succeeded)
                {
                    await _store.SetEventStatusAsync(info.Id, numbers, SubscriptionEventStatus.Delivered, cancellationToken);
                    await _store.SetLastNotificationAsync(info.Id, Clock.UtcNow, cancellationToken);
                    sent = true;
                    continue;
                }

                int attempts = authorized.Max(e => e.Attempts) + 1;
                _logger.LogInformation("Delivery to subscription {SubscriptionId} failed (attempt {Attempt}): {Error}", info.Id, attempts, result.Error);
                if (attempts >= _configuration.MaxDeliveryAttempts)
                {
                    // The events stay pending with their numbers, and are delivered after the subscriber resets the status to requested.
                    await _store.RecordFailedAttemptAsync(info.Id, numbers, Clock.UtcNow, result.Error, cancellationToken);
                    await SetStatusAsync(info, SubscriptionStatusCode.Error, string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionDeliveryFailed, attempts, result.Error), cancellationToken);
                    return;
                }

                double delaySeconds = _configuration.RetryBaseDelaySeconds * Math.Pow(2, attempts - 1);
                await _store.RecordFailedAttemptAsync(info.Id, numbers, Clock.UtcNow.AddSeconds(delaySeconds), result.Error, cancellationToken);
                return;
            }

            SubscriptionState current = await _store.GetStateAsync(info.Id, cancellationToken) ?? state;
            if (!sent && IsHeartbeatDue(info, current))
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

                if (heartbeat.Succeeded)
                {
                    await _store.SetLastNotificationAsync(info.Id, Clock.UtcNow, cancellationToken);
                }
                else
                {
                    await SetStatusAsync(info, SubscriptionStatusCode.Error, string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionHeartbeatFailed, heartbeat.Error), cancellationToken);
                }
            }
        }

        private async Task<ChannelDeliveryResult> SendAsync(RegisteredSubscription subscription, SubscriptionNotificationRequest request, CancellationToken cancellationToken)
        {
            if (!_channels.TryGetValue(subscription.Info.ChannelType, out ISubscriptionChannel channel))
            {
                return ChannelDeliveryResult.Failure(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionChannelNotSupported, subscription.Info.ChannelType));
            }

            string payload = _notificationFactory.Serialize(_notificationFactory.CreateNotification(request), subscription.Info.ContentType);
            long started = Stopwatch.GetTimestamp();
            ChannelDeliveryResult result = await channel.SendAsync(subscription.Info, payload, cancellationToken);
            _metrics?.NotificationSent(request.Type.ToString(), result.Succeeded, Stopwatch.GetElapsedTime(started));
            return result;
        }

        private async Task DeactivateAsync(string subscriptionId, SubscriptionState state, CancellationToken cancellationToken)
        {
            if (state?.ActivationTransactionId != null)
            {
                await _store.DeactivateAsync(subscriptionId, cancellationToken);
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
            _requestContextAccessor.RequestContext = new FhirRequestContext(
                method: "SubscriptionDelivery",
                uriString: "https://localhost/",
                baseUriString: "https://localhost/",
                correlationId: Activity.Current?.Id ?? Guid.NewGuid().ToString(),
                requestHeaders: new Dictionary<string, StringValues>(),
                responseHeaders: new Dictionary<string, StringValues>())
            {
                IsBackgroundTask = true,
            };
        }
    }
}
