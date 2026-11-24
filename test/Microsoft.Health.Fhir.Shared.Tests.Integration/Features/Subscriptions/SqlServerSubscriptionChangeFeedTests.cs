// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Messages.Delete;
using Microsoft.Health.Fhir.SqlServer.Features.Watchdogs;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.Integration.Features.Subscriptions
{
    /// <summary>
    /// Integration tests for the subscription event pump reading the <c>ResourceChangeData</c> change feed (ADR-2611, option 2).
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SqlServerSubscriptionChangeFeedTests : SqlServerSubscriptionTestBase, IClassFixture<SubscriptionChangeCaptureFixture>
    {
        public SqlServerSubscriptionChangeFeedTests(SubscriptionChangeCaptureFixture fixture, ITestOutputHelper output)
            : base(fixture.Storage, output)
        {
            Configuration.EventSource = SubscriptionEventSource.ResourceChangeData;
        }

        protected override bool ChangeCaptureEnabled => true;

        [Fact]
        public async Task GivenTheChangeFeedSource_WhenAResourceIsCreatedUpdatedAndDeleted_ThenEachVersionIsMatchedWithItsInteraction()
        {
            // Arrange
            SubscriptionEventWatchdog pump = CreatePump("true", SubscriptionInteraction.Create, SubscriptionInteraction.Update, SubscriptionInteraction.Delete);
            await pump.PumpAsync(CancellationToken.None); // initializes the cursor
            await ActivateSubscriptionAsync("s1");

            string id = await CreateObservationAsync("preliminary");
            await UpdateObservationAsync(id, "final");
            await Fixture.Mediator.DeleteResourceAsync(new ResourceKey("Observation", id), DeleteOperation.SoftDelete);

            // Act
            int processed = await pump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.True(processed >= 3);
            List<SubscriptionEventRecord> events = (await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None)).Where(e => e.ResourceId == id).ToList();
            Assert.Equal([1, 2, 3], events.Select(e => e.ResourceVersion));
            Assert.Equal([SubscriptionInteraction.Create, SubscriptionInteraction.Update, SubscriptionInteraction.Delete], events.Select(e => e.Interaction));
            Assert.Equal(events.Select(e => e.EventNumber).Order(), events.Select(e => e.EventNumber));
        }

        [Fact]
        public async Task GivenASubscriptionActivatedBeforeTheChangeFeedSourceFirstRuns_WhenPumped_ThenChangesAfterTheActivationAreMatched()
        {
            // Arrange
            SubscriptionEventWatchdog pump = CreatePump("true");
            await ActivateSubscriptionAsync("s1");
            string id = await CreateObservationAsync("preliminary");

            // Act
            await pump.PumpAsync(CancellationToken.None); // initializes the cursor
            await pump.PumpAsync(CancellationToken.None);

            // Assert
            SubscriptionEventRecord e = Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None), r => r.ResourceId == id);
            Assert.Equal(SubscriptionEventStatus.Pending, e.Status);
        }

        [Fact]
        public async Task GivenEventsFromTheTransactionsSource_WhenTheSourceIsSwitchedToTheChangeFeed_ThenTheSameChangesAreNotNumberedAgain()
        {
            // Arrange: match a change through the Transactions source first.
            Configuration.EventSource = SubscriptionEventSource.Transactions;
            SubscriptionEventWatchdog pump = CreatePump("true");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            await ActivateSubscriptionAsync("s1");
            string id = await CreateObservationAsync("preliminary");
            await AdvanceVisibilityAsync();
            await pump.PumpAsync(CancellationToken.None);
            Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None), r => r.ResourceId == id);

            // Act: the change feed source starts before the activation, so it reads the same change again.
            Configuration.EventSource = SubscriptionEventSource.ResourceChangeData;
            SubscriptionEventWatchdog changeFeedPump = CreatePump("true");
            await changeFeedPump.PumpAsync(CancellationToken.None);
            await changeFeedPump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.Single(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None), r => r.ResourceId == id);
        }
    }
}
