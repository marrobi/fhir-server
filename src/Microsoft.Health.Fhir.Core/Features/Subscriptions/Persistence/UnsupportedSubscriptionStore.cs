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
    /// The subscription store for data stores without subscription support. Subscriptions require SQL Server (ADR-2611).
    /// </summary>
    public sealed class UnsupportedSubscriptionStore : ISubscriptionStore
    {
        public SubscriptionStoreAvailability Availability => SubscriptionStoreAvailability.NotSupported;

        public Task<IReadOnlyList<SubscriptionState>> GetStatesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SubscriptionState>>([]);

        public Task<SubscriptionState> GetStateAsync(string subscriptionId, CancellationToken cancellationToken) => Task.FromResult<SubscriptionState>(null);

        public Task SaveGrantAsync(string subscriptionId, string contentHash, string grant, CancellationToken cancellationToken) => throw NotSupported();

        public Task DeleteStateAsync(string subscriptionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<long> ActivateAsync(string subscriptionId, CancellationToken cancellationToken) => throw NotSupported();

        public Task DeactivateAsync(string subscriptionId, bool revokeGrant, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireLeaseAsync(string subscriptionId, string worker, TimeSpan leasePeriod, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ReleaseLeaseAsync(string subscriptionId, string worker, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SubscriptionEventRecord>> GetPendingEventsAsync(string subscriptionId, int maxCount, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SubscriptionEventRecord>>([]);

        public Task SetEventStatusAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, SubscriptionEventStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordFailedAttemptAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetErrorAsync(string subscriptionId, string error, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetLastNotificationAsync(string subscriptionId, DateTimeOffset sentUtc, int contactFailures, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordContactFailureAsync(string subscriptionId, int contactFailures, DateTimeOffset nextContactUtc, string error, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SubscriptionBacklog> GetBacklogAsync(CancellationToken cancellationToken) => Task.FromResult(new SubscriptionBacklog(0, null));

        public Task<SubscriptionEventCounts> GetEventCountsAsync(string subscriptionId, CancellationToken cancellationToken) => Task.FromResult(new SubscriptionEventCounts(0, 0));

        public Task<IReadOnlyList<string>> GetSubscriptionsWithDueEventsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        private static NotSupportedException NotSupported() => new(Core.Resources.SubscriptionsRequireSqlServer);
    }
}
