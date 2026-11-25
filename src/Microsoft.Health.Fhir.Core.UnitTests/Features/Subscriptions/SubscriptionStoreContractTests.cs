// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// The <see cref="ISubscriptionStore"/> contract. Each store implementation runs these tests, so the in-memory store used by
    /// unit tests cannot drift from the SQL store.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public abstract class SubscriptionStoreContractTests
    {
        protected abstract ISubscriptionStore Store { get; }

        /// <summary>
        /// Records pending events for an activated subscription, as the event pump does, in event-number order.
        /// </summary>
        /// <param name="subscriptionId">The subscription.</param>
        /// <param name="count">The number of events.</param>
        /// <returns>The event numbers.</returns>
        protected abstract Task<IReadOnlyList<long>> AddPendingEventsAsync(string subscriptionId, int count);

        [Fact]
        public async Task GivenAGrant_WhenTheContentHashChanges_ThenTheActivationIsCleared()
        {
            // Arrange
            await Store.SaveGrantAsync("s1", "hash-a", "{}", CancellationToken.None);
            long activation = await Store.ActivateAsync("s1", CancellationToken.None);

            // Act
            await Store.SaveGrantAsync("s1", "hash-a", "{\"a\":1}", CancellationToken.None);
            SubscriptionState sameContent = await Store.GetStateAsync("s1", CancellationToken.None);
            await Store.SaveGrantAsync("s1", "hash-b", "{}", CancellationToken.None);
            SubscriptionState newContent = await Store.GetStateAsync("s1", CancellationToken.None);

            // Assert
            Assert.Equal(activation, sameContent.ActivationTransactionId);
            Assert.Equal("{\"a\":1}", sameContent.Grant);
            Assert.Null(newContent.ActivationTransactionId);
            Assert.Equal("hash-b", newContent.ContentHash);
        }

        [Fact]
        public async Task GivenPendingEvents_WhenTheSubscriptionContentChanges_ThenTheyAreRetired()
        {
            // Arrange
            await GrantAndActivateAsync("s1", "h1");
            await AddPendingEventsAsync("s1", 2);

            // Act: an API update changes the endpoint, so the hash changes and the subscription is activated again.
            await Store.SaveGrantAsync("s1", "h2", "{}", CancellationToken.None);
            await Store.ActivateAsync("s1", CancellationToken.None);

            // Assert
            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
        }

        [Fact]
        public async Task GivenPendingEvents_WhenTheSameContentIsSavedAgain_ThenTheyStayPending()
        {
            await GrantAndActivateAsync("s1", "h1");
            await AddPendingEventsAsync("s1", 1);

            await Store.SaveGrantAsync("s1", "h1", "{}", CancellationToken.None);

            Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
        }

        [Fact]
        public async Task GivenAnActivatedSubscription_WhenDeactivatedWithRevocation_ThenTheGrantIsRemovedAndPendingEventsAreRetired()
        {
            // Arrange
            await GrantAndActivateAsync("s1", "h1");
            await AddPendingEventsAsync("s1", 1);

            // Act
            await Store.DeactivateAsync("s1", revokeGrant: true, CancellationToken.None);

            // Assert
            SubscriptionState state = await Store.GetStateAsync("s1", CancellationToken.None);
            Assert.Null(state.ActivationTransactionId);
            Assert.Null(state.Grant);
            Assert.Null(state.ContentHash);
            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
        }

        [Fact]
        public async Task GivenPendingEvents_WhenDueSubscriptionsAreListed_ThenOnlyActivatedSubscriptionsWithADueEventAreReturned()
        {
            // Arrange: s2 has a pending event but was deactivated without revocation, so its events are kept but not due.
            await GrantAndActivateAsync("s1");
            await AddPendingEventsAsync("s1", 1);
            await GrantAndActivateAsync("s2");
            await AddPendingEventsAsync("s2", 1);
            await Store.DeactivateAsync("s2", revokeGrant: false, CancellationToken.None);

            // Act
            IReadOnlyList<string> due = await Store.GetSubscriptionsWithDueEventsAsync(CancellationToken.None);

            // Assert
            Assert.Equal(["s1"], due);
        }

        [Fact]
        public async Task GivenTheOldestPendingEventIsBackingOff_WhenDueSubscriptionsAreListed_ThenTheSubscriptionIsNotDue()
        {
            // Arrange: delivery is in event-number order, so later events wait for the oldest one even when their own time has come.
            await GrantAndActivateAsync("s1");
            IReadOnlyList<long> numbers = await AddPendingEventsAsync("s1", 2);
            await Store.RecordFailedAttemptAsync("s1", [numbers[0]], DateTimeOffset.UtcNow.AddMinutes(5), "HTTP 503", CancellationToken.None);

            // Act
            IReadOnlyList<string> due = await Store.GetSubscriptionsWithDueEventsAsync(CancellationToken.None);

            // Assert
            Assert.Empty(due);
        }

        [Fact]
        public async Task GivenPendingEvents_WhenAttemptsFailAndSucceed_ThenStatusAttemptsAndDueTimesAreTracked()
        {
            // Arrange
            await GrantAndActivateAsync("s1");
            IReadOnlyList<long> numbers = await AddPendingEventsAsync("s1", 3);

            // Act
            await Store.RecordFailedAttemptAsync("s1", [numbers[0]], DateTimeOffset.UtcNow.AddMinutes(5), "HTTP 503", CancellationToken.None);
            await Store.SetEventStatusAsync("s1", [numbers[1]], SubscriptionEventStatus.Delivered, CancellationToken.None);
            await Store.SetEventStatusAsync("s1", [numbers[2]], SubscriptionEventStatus.Unevaluable, CancellationToken.None);

            // Assert
            SubscriptionEventRecord first = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(numbers[0], first.EventNumber);
            Assert.Equal(1, first.Attempts);
            Assert.Empty(await Store.GetSubscriptionsWithDueEventsAsync(CancellationToken.None));
            Assert.Equal(new SubscriptionEventCounts(1, 1), await Store.GetEventCountsAsync("s1", CancellationToken.None));

            // Activation makes retained events due again.
            await Store.ActivateAsync("s1", CancellationToken.None);
            Assert.Equal(["s1"], await Store.GetSubscriptionsWithDueEventsAsync(CancellationToken.None));
            Assert.Equal(0, Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None)).Attempts);
        }

        [Fact]
        public async Task GivenALease_WhenAnotherWorkerTries_ThenItIsExclusiveUntilReleased()
        {
            Assert.True(await Store.TryAcquireLeaseAsync("s1", "w1", TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.True(await Store.TryAcquireLeaseAsync("s1", "w1", TimeSpan.FromMinutes(1), CancellationToken.None));
            Assert.False(await Store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromMinutes(1), CancellationToken.None));

            await Store.ReleaseLeaseAsync("s1", "w2", CancellationToken.None);
            Assert.False(await Store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromMinutes(1), CancellationToken.None));

            await Store.ReleaseLeaseAsync("s1", "w1", CancellationToken.None);
            Assert.True(await Store.TryAcquireLeaseAsync("s1", "w2", TimeSpan.FromMinutes(1), CancellationToken.None));
        }

        [Fact]
        public async Task GivenASubscription_WhenItsStateIsDeleted_ThenItsEventsAreDeletedToo()
        {
            await GrantAndActivateAsync("s1");
            await AddPendingEventsAsync("s1", 1);
            await Store.SetErrorAsync("s1", "boom", CancellationToken.None);
            Assert.Equal("boom", (await Store.GetStateAsync("s1", CancellationToken.None)).Error);

            await Store.DeleteStateAsync("s1", CancellationToken.None);

            Assert.Null(await Store.GetStateAsync("s1", CancellationToken.None));
            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Equal(new SubscriptionEventCounts(0, 0), await Store.GetEventCountsAsync("s1", CancellationToken.None));
        }

        [Fact]
        public async Task GivenContactFailures_WhenANotificationSucceedsOrTheSubscriptionIsWrittenAgain_ThenTheBackOffIsCleared()
        {
            // Arrange
            await GrantAndActivateAsync("s1");
            DateTimeOffset next = DateTimeOffset.UtcNow.AddMinutes(5);

            // Act
            await Store.RecordContactFailureAsync("s1", 3, next, "HTTP 503", CancellationToken.None);
            SubscriptionState failed = await Store.GetStateAsync("s1", CancellationToken.None);
            await Store.SetLastNotificationAsync("s1", DateTimeOffset.UtcNow, 0, CancellationToken.None);
            SubscriptionState notified = await Store.GetStateAsync("s1", CancellationToken.None);
            await Store.RecordContactFailureAsync("s1", 4, next, "HTTP 503", CancellationToken.None);
            await Store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);
            SubscriptionState rewritten = await Store.GetStateAsync("s1", CancellationToken.None);

            // Assert
            Assert.Equal(3, failed.ContactFailures);
            Assert.InRange(failed.NextContactUtc.Value, next.AddSeconds(-1), next.AddSeconds(1));
            Assert.Equal("HTTP 503", failed.Error);
            Assert.Equal(0, notified.ContactFailures);
            Assert.Null(notified.NextContactUtc);
            Assert.Null(notified.Error);
            Assert.Equal(0, rewritten.ContactFailures);
            Assert.Null(rewritten.NextContactUtc);
            Assert.NotNull(rewritten.ActivationTransactionId);
        }

        [Fact]
        public async Task GivenPendingEvents_WhenTheBacklogIsRead_ThenItCountsThemAndReportsTheOldest()
        {
            // Arrange: other subscriptions may share the store, so compare with the backlog before.
            await GrantAndActivateAsync("s1");
            SubscriptionBacklog before = await Store.GetBacklogAsync(CancellationToken.None);

            // Act
            await AddPendingEventsAsync("s1", 3);
            SubscriptionBacklog after = await Store.GetBacklogAsync(CancellationToken.None);

            // Assert
            Assert.Equal(before.PendingEvents + 3, after.PendingEvents);
            Assert.NotNull(after.OldestPendingEventUtc);
        }

        private async Task GrantAndActivateAsync(string subscriptionId, string contentHash = "h")
        {
            await Store.SaveGrantAsync(subscriptionId, contentHash, "{}", CancellationToken.None);
            await Store.ActivateAsync(subscriptionId, CancellationToken.None);
        }
    }
}
