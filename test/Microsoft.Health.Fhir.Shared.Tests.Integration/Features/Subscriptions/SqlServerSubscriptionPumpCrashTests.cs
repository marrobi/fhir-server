// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.Integration.Persistence;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.Integration.Features.Subscriptions
{
    /// <summary>
    /// Kills the subscription event pump part-way through its batches and restarts it, then checks that the outbox holds every
    /// change exactly once, in order and with contiguous event numbers (ADR-2611: events are committed with the cursor).
    /// </summary>
    [FhirStorageTestsFixtureArgumentSets(DataStore.SqlServer)]
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SqlServerSubscriptionPumpCrashTests : SqlServerSubscriptionTestBase, IClassFixture<FhirStorageTestsFixture>
    {
        private const int Changes = 40;

        public SqlServerSubscriptionPumpCrashTests(FhirStorageTestsFixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            Configuration.PumpTransactionBatchSize = 3;
        }

        [Fact]
        public async Task GivenThePumpDiesAfterMatchingAPageButBeforeCommittingIt_WhenRestarted_ThenEveryChangeIsNumberedExactlyOnce()
        {
            // Arrange
            List<string> ids = await ArrangeChangesAsync();

            // Act: every run is a new pump, as after a process restart, and dies after matching its second page.
            int crashes = 0;
            for (int run = 0; run < 100; run++)
            {
                int pages = 0;
                WrapSource = source => new CrashingEventSource(source, () => ++pages == 2);
                try
                {
                    if (await CreatePump("true", SubscriptionInteraction.Create).PumpAsync(CancellationToken.None) == 0)
                    {
                        break;
                    }
                }
                catch (InvalidOperationException ex) when (ex.Message == CrashingEventSource.Message)
                {
                    crashes++;
                }
            }

            // Assert
            Assert.True(crashes >= Changes / Configuration.PumpTransactionBatchSize / 2, $"Only {crashes} crashes were injected.");
            await AssertEachChangeNumberedOnceAsync(ids);
        }

        [Fact]
        public async Task GivenThePumpIsStoppedAtRandomPoints_WhenRestarted_ThenEveryChangeIsNumberedExactlyOnce()
        {
            // Arrange
            List<string> ids = await ArrangeChangesAsync();

            // Act: cancelling aborts in-flight SQL, including a commit in progress, which rolls back as a dropped connection does.
            var random = new Random(20261124);
            int interrupted = 0;
            for (int run = 0; run < 200; run++)
            {
                bool last = run >= 150;
                using var stop = new CancellationTokenSource();
                if (!last)
                {
                    stop.CancelAfter(TimeSpan.FromMilliseconds(random.Next(5, 120)));
                }

                try
                {
                    if (await CreatePump("true", SubscriptionInteraction.Create).PumpAsync(stop.Token) == 0 && !stop.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch (Exception) when (stop.IsCancellationRequested)
                {
                    interrupted++;
                }
            }

            // Assert
            Output.WriteLine($"{interrupted} runs were interrupted.");
            Assert.True(interrupted > 0);
            await AssertEachChangeNumberedOnceAsync(ids);
        }

        private async System.Threading.Tasks.Task<List<string>> ArrangeChangesAsync()
        {
            await AdvanceVisibilityAsync();
            await CreatePump("true", SubscriptionInteraction.Create).PumpAsync(CancellationToken.None); // initializes the cursor
            await ActivateSubscriptionAsync("s1");

            var ids = new List<string>();
            for (int i = 0; i < Changes; i++)
            {
                ids.Add(await CreateObservationAsync("final"));
            }

            await AdvanceVisibilityAsync();
            return ids;
        }

        private async Task AssertEachChangeNumberedOnceAsync(List<string> ids)
        {
            Output.WriteLine(Dump("SELECT EventNumber, TransactionId, ResourceSurrogateId, ResourceId FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1' ORDER BY EventNumber"));
            IReadOnlyList<SubscriptionEventRecord> events = await Store.GetPendingEventsAsync("s1", 1000, CancellationToken.None);
            Assert.Equal(Changes, ExecuteScalar<int>("SELECT count(*) FROM dbo.SubscriptionEvents WHERE SubscriptionId = 's1'"));
            Assert.Equal(ids.Order(), events.Select(e => e.ResourceId).Order());
            Assert.Equal(Enumerable.Range(1, Changes).Select(n => (long)n), events.Select(e => e.EventNumber));
            Assert.Equal(events.Select(e => e.TransactionId).Order(), events.Select(e => e.TransactionId));
            Assert.Equal(Changes, (await Store.GetStateAsync("s1", CancellationToken.None)).LastEventNumber);
        }

        /// <summary>
        /// Delegates to an event source and fails after a page has been read when told to, before the pump can commit it.
        /// </summary>
        private sealed class CrashingEventSource : ITransactionEventSource
        {
            public const string Message = "Simulated process crash.";

            private readonly ITransactionEventSource _inner;
            private readonly Func<bool> _crashAfterRead;

            public CrashingEventSource(ITransactionEventSource inner, Func<bool> crashAfterRead)
            {
                _inner = inner;
                _crashAfterRead = crashAfterRead;
            }

            public SubscriptionEventSource Kind => _inner.Kind;

            public string CursorId => _inner.CursorId;

            public string GetUnavailableReason() => _inner.GetUnavailableReason();

            public System.Threading.Tasks.Task<long> GetStartPositionAsync(long? earliestActivationTransactionId, CancellationToken cancellationToken) =>
                _inner.GetStartPositionAsync(earliestActivationTransactionId, cancellationToken);

            public async System.Threading.Tasks.Task<TransactionEventPage> ReadAsync(long position, int maxPositions, Func<string, bool> isCovered, CancellationToken cancellationToken)
            {
                TransactionEventPage page = await _inner.ReadAsync(position, maxPositions, isCovered, cancellationToken);
                if (page.PositionCount > 0 && page.Changes.Count > 0 && _crashAfterRead())
                {
                    throw new InvalidOperationException(Message);
                }

                return page;
            }
        }
    }
}
