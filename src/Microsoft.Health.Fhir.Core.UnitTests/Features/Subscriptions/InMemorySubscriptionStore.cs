// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// In-memory subscription store with the same contract as the SQL store.
    /// </summary>
    public sealed class InMemorySubscriptionStore : ISubscriptionStore
    {
        private readonly object _sync = new();

        public Dictionary<string, SubscriptionState> States { get; } = new(StringComparer.Ordinal);

        public List<SubscriptionEventRecord> Events { get; } = [];

        public Dictionary<string, (string Worker, DateTimeOffset End)> Leases { get; } = new(StringComparer.Ordinal);

        public long Visibility { get; set; } = 1000;

        public SubscriptionStoreAvailability Availability { get; set; } = SubscriptionStoreAvailability.Available;

        public List<(string SubscriptionId, string Worker, TimeSpan Period)> LeaseRequests { get; } = [];

        /// <summary>
        /// Gets or sets a worker that takes over the lease the next time its holder tries to renew it.
        /// </summary>
        public string LeaseTakenOnRenewal { get; set; }

        public Task<IReadOnlyList<SubscriptionState>> GetStatesAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<SubscriptionState>>(States.Values.ToList());
            }
        }

        public Task<SubscriptionState> GetStateAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult(States.GetValueOrDefault(subscriptionId));
            }
        }

        public Task SaveGrantAsync(string subscriptionId, string contentHash, string grant, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                SubscriptionState current = States.GetValueOrDefault(subscriptionId);
                if ((current?.ContentHash ?? string.Empty) != contentHash)
                {
                    RetirePending(subscriptionId);
                }

                States[subscriptionId] = new SubscriptionState
                {
                    SubscriptionId = subscriptionId,
                    ContentHash = contentHash,
                    Grant = grant,
                    ActivationTransactionId = current?.ContentHash == contentHash ? current.ActivationTransactionId : null,
                    LastEventNumber = current?.LastEventNumber ?? 0,
                    LastNotificationUtc = current?.LastNotificationUtc,
                    Error = current?.Error,
                };
            }

            return Task.CompletedTask;
        }

        public Task DeleteStateAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                States.Remove(subscriptionId);
                Events.RemoveAll(e => e.SubscriptionId == subscriptionId);
            }

            return Task.CompletedTask;
        }

        public Task<long> ActivateAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                SubscriptionState current = States.GetValueOrDefault(subscriptionId) ?? new SubscriptionState { SubscriptionId = subscriptionId };
                long activation = current.ActivationTransactionId ?? Visibility;
                Update(subscriptionId, s => With(s, activation: activation));
                for (int i = 0; i < Events.Count; i++)
                {
                    if (Events[i].SubscriptionId == subscriptionId && Events[i].Status == SubscriptionEventStatus.Pending)
                    {
                        Events[i] = WithEvent(Events[i], attempts: 0, next: Clock.UtcNow);
                    }
                }

                return Task.FromResult(activation);
            }
        }

        public Task DeactivateAsync(string subscriptionId, bool revokeGrant, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Update(subscriptionId, s => With(s, activation: null, clearActivation: true));
                if (revokeGrant && States.TryGetValue(subscriptionId, out SubscriptionState state))
                {
                    States[subscriptionId] = new SubscriptionState
                    {
                        SubscriptionId = state.SubscriptionId,
                        LastEventNumber = state.LastEventNumber,
                        LastNotificationUtc = state.LastNotificationUtc,
                        Error = state.Error,
                    };
                    RetirePending(subscriptionId);
                }
            }

            return Task.CompletedTask;
        }

        private void RetirePending(string subscriptionId)
        {
            for (int i = 0; i < Events.Count; i++)
            {
                if (Events[i].SubscriptionId == subscriptionId && Events[i].Status == SubscriptionEventStatus.Pending)
                {
                    Events[i] = WithEvent(Events[i], status: SubscriptionEventStatus.NotAuthorized);
                }
            }
        }

        public Task<bool> TryAcquireLeaseAsync(string subscriptionId, string worker, TimeSpan leasePeriod, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                LeaseRequests.Add((subscriptionId, worker, leasePeriod));
                if (LeaseTakenOnRenewal != null && Leases.TryGetValue(subscriptionId, out var held) && held.Worker == worker)
                {
                    Leases[subscriptionId] = (LeaseTakenOnRenewal, Clock.UtcNow.AddHours(1));
                    LeaseTakenOnRenewal = null;
                    return Task.FromResult(false);
                }

                if (Leases.TryGetValue(subscriptionId, out var lease) && lease.Worker != worker && lease.End > Clock.UtcNow)
                {
                    return Task.FromResult(false);
                }

                Leases[subscriptionId] = (worker, Clock.UtcNow + leasePeriod);
                return Task.FromResult(true);
            }
        }

        public Task ReleaseLeaseAsync(string subscriptionId, string worker, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                if (Leases.TryGetValue(subscriptionId, out var lease) && lease.Worker == worker)
                {
                    Leases.Remove(subscriptionId);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SubscriptionEventRecord>> GetPendingEventsAsync(string subscriptionId, int maxCount, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult<IReadOnlyList<SubscriptionEventRecord>>(Events.Where(e => e.SubscriptionId == subscriptionId && e.Status == SubscriptionEventStatus.Pending).OrderBy(e => e.EventNumber).Take(maxCount).ToList());
            }
        }

        public Task SetEventStatusAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, SubscriptionEventStatus status, CancellationToken cancellationToken)
        {
            UpdateEvents(subscriptionId, eventNumbers, e => WithEvent(e, status: status));
            return Task.CompletedTask;
        }

        public Task RecordFailedAttemptAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken)
        {
            UpdateEvents(subscriptionId, eventNumbers, e => WithEvent(e, attempts: e.Attempts + 1, next: nextAttemptUtc));
            return Task.CompletedTask;
        }

        public Task SetErrorAsync(string subscriptionId, string error, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Update(subscriptionId, s => With(s, error: error ?? string.Empty));
            }

            return Task.CompletedTask;
        }

        public Task SetLastNotificationAsync(string subscriptionId, DateTimeOffset sentUtc, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Update(subscriptionId, s => With(s, lastNotification: sentUtc));
            }

            return Task.CompletedTask;
        }

        public Task<SubscriptionEventCounts> GetEventCountsAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult(new SubscriptionEventCounts(
                    Events.Count(e => e.SubscriptionId == subscriptionId && e.Status == SubscriptionEventStatus.Pending),
                    Events.Count(e => e.SubscriptionId == subscriptionId && e.Status == SubscriptionEventStatus.Unevaluable)));
            }
        }

        public Task<IReadOnlyList<string>> GetSubscriptionsWithDueEventsAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                // As GetSubscriptionsWithDueEvents: activated subscriptions whose oldest pending event is due.
                return Task.FromResult<IReadOnlyList<string>>(States.Values
                    .Where(s => s.ActivationTransactionId != null)
                    .Where(s => Events
                        .Where(e => e.SubscriptionId == s.SubscriptionId && e.Status == SubscriptionEventStatus.Pending)
                        .OrderBy(e => e.EventNumber)
                        .FirstOrDefault() is { } oldest && oldest.NextAttemptUtc <= Clock.UtcNow)
                    .Select(s => s.SubscriptionId)
                    .ToList());
            }
        }

        public void AddEvent(string subscriptionId, string resourceType, string resourceId, int version, SubscriptionInteraction interaction = SubscriptionInteraction.Update)
        {
            lock (_sync)
            {
                SubscriptionState state = States[subscriptionId];
                long number = state.LastEventNumber + 1;
                States[subscriptionId] = With(state, lastEventNumber: number);
                Events.Add(new SubscriptionEventRecord
                {
                    SubscriptionId = subscriptionId,
                    EventNumber = number,
                    TransactionId = number,
                    ResourceType = resourceType,
                    ResourceId = resourceId,
                    ResourceVersion = version,
                    Interaction = interaction,
                    Status = SubscriptionEventStatus.Pending,
                    NextAttemptUtc = Clock.UtcNow.AddSeconds(-1),
                    EventUtc = Clock.UtcNow,
                });
            }
        }

        private void Update(string id, Func<SubscriptionState, SubscriptionState> update)
        {
            SubscriptionState current = States.GetValueOrDefault(id) ?? new SubscriptionState { SubscriptionId = id };
            States[id] = update(current);
        }

        private void UpdateEvents(string subscriptionId, IReadOnlyList<long> numbers, Func<SubscriptionEventRecord, SubscriptionEventRecord> update)
        {
            lock (_sync)
            {
                for (int i = 0; i < Events.Count; i++)
                {
                    if (Events[i].SubscriptionId == subscriptionId && numbers.Contains(Events[i].EventNumber))
                    {
                        Events[i] = update(Events[i]);
                    }
                }
            }
        }

        private static SubscriptionState With(SubscriptionState s, long? activation = null, bool clearActivation = false, long? lastEventNumber = null, DateTimeOffset? lastNotification = null, string error = null) => new()
        {
            SubscriptionId = s.SubscriptionId,
            ContentHash = s.ContentHash,
            Grant = s.Grant,
            ActivationTransactionId = clearActivation ? null : activation ?? s.ActivationTransactionId,
            LastEventNumber = lastEventNumber ?? s.LastEventNumber,
            LastNotificationUtc = lastNotification ?? s.LastNotificationUtc,
            Error = error == null ? s.Error : error.Length == 0 ? null : error,
        };

        private static SubscriptionEventRecord WithEvent(SubscriptionEventRecord e, SubscriptionEventStatus? status = null, int? attempts = null, DateTimeOffset? next = null) => new()
        {
            SubscriptionId = e.SubscriptionId,
            EventNumber = e.EventNumber,
            TransactionId = e.TransactionId,
            ResourceType = e.ResourceType,
            ResourceId = e.ResourceId,
            ResourceVersion = e.ResourceVersion,
            Interaction = e.Interaction,
            Status = status ?? e.Status,
            Attempts = attempts ?? e.Attempts,
            NextAttemptUtc = next ?? e.NextAttemptUtc,
            EventUtc = e.EventUtc,
        };
    }
}
