// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence
{
    /// <summary>
    /// Durable storage for subscription runtime state and the per-subscription event outbox.
    /// </summary>
    public interface ISubscriptionStore
    {
        /// <summary>
        /// Gets whether the store can be used now. Callers must not use it otherwise.
        /// </summary>
        SubscriptionStoreAvailability Availability { get; }

        Task<IReadOnlyList<SubscriptionState>> GetStatesAsync(CancellationToken cancellationToken);

        Task<SubscriptionState> GetStateAsync(string subscriptionId, CancellationToken cancellationToken);

        /// <summary>
        /// Stores the grant. When the content hash differs from the stored one, the activation watermark is cleared,
        /// so the subscription must complete a new handshake before it is matched again.
        /// </summary>
        Task SaveGrantAsync(string subscriptionId, string contentHash, string grant, CancellationToken cancellationToken);

        /// <summary>
        /// Deletes the state and every outbox event of a subscription.
        /// </summary>
        Task DeleteStateAsync(string subscriptionId, CancellationToken cancellationToken);

        /// <summary>
        /// Sets the activation watermark to the current transaction visibility, if not already set, and returns it.
        /// </summary>
        Task<long> ActivateAsync(string subscriptionId, CancellationToken cancellationToken);

        /// <summary>
        /// Clears the activation watermark, so no further transactions are matched. With <paramref name="revokeGrant"/>, also removes
        /// the grant and retires pending events, so the subscription cannot be activated again without an authorized API write.
        /// </summary>
        Task DeactivateAsync(string subscriptionId, bool revokeGrant, CancellationToken cancellationToken);

        Task<bool> TryAcquireLeaseAsync(string subscriptionId, string worker, TimeSpan leasePeriod, CancellationToken cancellationToken);

        Task ReleaseLeaseAsync(string subscriptionId, string worker, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the oldest pending events in event-number order.
        /// </summary>
        Task<IReadOnlyList<SubscriptionEventRecord>> GetPendingEventsAsync(string subscriptionId, int maxCount, CancellationToken cancellationToken);

        Task SetEventStatusAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, SubscriptionEventStatus status, CancellationToken cancellationToken);

        /// <summary>
        /// Records a failed delivery attempt and schedules the next one.
        /// </summary>
        Task RecordFailedAttemptAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken);

        /// <summary>
        /// Stores the latest server-reported error; null clears it.
        /// </summary>
        Task SetErrorAsync(string subscriptionId, string error, CancellationToken cancellationToken);

        /// <summary>
        /// Records a successful notification: clears the scheduled contact and the reported error, and sets the consecutive
        /// failure count. Delivered events and heartbeats reset it to zero. A recovery handshake keeps it, so an endpoint that
        /// answers handshakes but rejects events keeps backing off.
        /// </summary>
        Task SetLastNotificationAsync(string subscriptionId, DateTimeOffset sentUtc, int contactFailures, CancellationToken cancellationToken);

        /// <summary>
        /// Records a failed handshake, heartbeat or recovery check: the consecutive failure count, when the endpoint is next
        /// contacted, and the error reported by $status.
        /// </summary>
        Task RecordContactFailureAsync(string subscriptionId, int contactFailures, DateTimeOffset nextContactUtc, string error, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the pending events across all subscriptions, for monitoring.
        /// </summary>
        Task<SubscriptionBacklog> GetBacklogAsync(CancellationToken cancellationToken);

        Task<SubscriptionEventCounts> GetEventCountsAsync(string subscriptionId, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the subscriptions whose oldest pending event is due for an attempt.
        /// </summary>
        Task<IReadOnlyList<string>> GetSubscriptionsWithDueEventsAsync(CancellationToken cancellationToken);
    }
}
