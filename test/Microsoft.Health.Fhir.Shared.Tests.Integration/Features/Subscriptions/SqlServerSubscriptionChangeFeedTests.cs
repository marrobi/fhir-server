// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Messages.Delete;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Subscriptions;
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
        public async Task GivenChangeRecordsOfOneWriteWhoseVersionsAreGone_WhenPumped_ThenEachIsRecordedAsADistinctUnevaluableEvent()
        {
            // Arrange: two change records with the same timestamp, as one write produces, for versions that are no longer stored.
            SubscriptionEventWatchdog pump = CreatePump("true", SubscriptionInteraction.Create);
            await pump.PumpAsync(CancellationToken.None); // initializes the cursor
            await ActivateSubscriptionAsync("s1");
            string first = Guid.NewGuid().ToString();
            string second = Guid.NewGuid().ToString();
            ExecuteSql($@"
DECLARE @TypeId smallint = (SELECT ResourceTypeId FROM dbo.ResourceType WHERE Name = 'Observation')
DECLARE @Now datetime2(7) = sysutcdatetime()
INSERT INTO dbo.ResourceChangeData (Timestamp, ResourceId, ResourceTypeId, ResourceVersion, ResourceChangeTypeId)
  VALUES (@Now, '{first}', @TypeId, 1, 0), (@Now, '{second}', @TypeId, 1, 0)");

            // Act
            await pump.PumpAsync(CancellationToken.None);

            // Assert
            Output.WriteLine(Dump("SELECT 'state', SubscriptionId, ActivationTransactionId FROM dbo.SubscriptionStates"));
            Output.WriteLine(Dump($"SELECT 'cursor', Id, Bigint, Char FROM dbo.Parameters WHERE Id LIKE 'SubscriptionEventWatchdog%'"));
            Output.WriteLine(Dump($"SELECT TOP 5 'change', Id, Timestamp, ResourceId FROM dbo.ResourceChangeData ORDER BY Id DESC"));
            Output.WriteLine(Dump($"SELECT ResourceId, Status, TransactionId, ResourceSurrogateId FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1' AND ResourceId IN ('{first}', '{second}')"));
            Assert.Equal(2, ExecuteScalar<int>($"SELECT count(DISTINCT ResourceSurrogateId) FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1' AND ResourceId IN ('{first}', '{second}') AND Status = {(int)SubscriptionEventStatus.Unevaluable}"));
            Assert.Equal(1, ExecuteScalar<int>($"SELECT count(DISTINCT TransactionId) FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1' AND ResourceId IN ('{first}', '{second}')"));
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
        public async Task GivenAPageThatIsReadAgain_WhenItSpansOlderPartitions_ThenNoRecordIsSkipped()
        {
            // Arrange: a change recorded five hours ago and a current one, as on a quiet server; they are in different hour partitions.
            ResourceChangeDataEventSource source = CreateChangeFeedSource();
            long start = ExecuteScalar<long>("SELECT isnull(max(Id), 0) FROM dbo.ResourceChangeData");
            ExecuteSql($@"
DECLARE @TypeId smallint = (SELECT ResourceTypeId FROM dbo.ResourceType WHERE Name = 'Observation')
INSERT INTO dbo.ResourceChangeData (Timestamp, ResourceId, ResourceTypeId, ResourceVersion, ResourceChangeTypeId)
  VALUES (dateadd(hour, -5, sysutcdatetime()), '{Guid.NewGuid()}', @TypeId, 1, 0)
INSERT INTO dbo.ResourceChangeData (Timestamp, ResourceId, ResourceTypeId, ResourceVersion, ResourceChangeTypeId)
  VALUES (sysutcdatetime(), '{Guid.NewGuid()}', @TypeId, 1, 0)");
            TransactionEventPage first = await source.ReadAsync(start, 10, null, CancellationToken.None);

            // Act: the page was not committed, for example because a subscription was activated meanwhile, so it is read again.
            TransactionEventPage retried = await source.ReadAsync(start, 10, null, CancellationToken.None);

            // Assert
            Assert.Equal(2, first.PositionCount);
            Assert.Equal(2, retried.PositionCount);
            Assert.Equal(first.LastPosition, retried.LastPosition);
        }

        [Fact]
        public async Task GivenADatabaseStartedWithTheTransactionsSource_WhenTheChangeFeedSourceIsConfigured_ThenThePumpDoesNotRunAndKeepsTheCursor()
        {
            // Arrange
            Configuration.EventSource = SubscriptionEventSource.Transactions;
            await AdvanceVisibilityAsync();
            await CreatePump("true").PumpAsync(CancellationToken.None);
            long? transactionsCursor = await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None);
            await ActivateSubscriptionAsync("s1");
            await CreateObservationAsync("preliminary");

            // Act
            Configuration.EventSource = SubscriptionEventSource.ResourceChangeData;
            SubscriptionEventWatchdog changeFeedPump = CreatePump("true");
            int first = await changeFeedPump.PumpAsync(CancellationToken.None);
            int second = await changeFeedPump.PumpAsync(CancellationToken.None);

            // Assert
            Assert.Equal(0, first + second);
            Assert.Empty(await Store.GetPendingEventsAsync("s1", 10, CancellationToken.None));
            Assert.Null(await Store.GetCursorAsync(ResourceChangeDataEventSource.ChangeFeedCursorId, CancellationToken.None));
            Assert.Equal(transactionsCursor, await Store.GetCursorAsync(SqlServerSubscriptionStore.CursorId, CancellationToken.None));
            Assert.Equal(nameof(SubscriptionEventSource.Transactions), await Store.GetEventSourceAsync(CancellationToken.None));
        }
    }
}
