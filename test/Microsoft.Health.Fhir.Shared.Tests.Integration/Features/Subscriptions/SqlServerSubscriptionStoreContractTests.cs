// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions;
using Microsoft.Health.Fhir.SqlServer.Features.Subscriptions;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.Integration.Persistence;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Tests.Integration.Features.Subscriptions
{
    /// <summary>
    /// Runs the <see cref="ISubscriptionStore"/> contract against SQL Server; the same tests run against the in-memory store.
    /// </summary>
    [FhirStorageTestsFixtureArgumentSets(DataStore.SqlServer)]
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SqlServerSubscriptionStoreContractTests : SubscriptionStoreContractTests, IClassFixture<FhirStorageTestsFixture>, IAsyncLifetime
    {
        private readonly Harness _sql;

        public SqlServerSubscriptionStoreContractTests(FhirStorageTestsFixture fixture, ITestOutputHelper output)
        {
            _sql = new Harness(fixture, output);
        }

        protected override ISubscriptionStore Store => _sql.SubscriptionStore;

        public ValueTask InitializeAsync() => _sql.InitializeAsync();

        public ValueTask DisposeAsync() => _sql.DisposeAsync();

        protected override Task<IReadOnlyList<long>> AddPendingEventsAsync(string subscriptionId, int count) => _sql.AddPendingEventsAsync(subscriptionId, count);

        private sealed class Harness : SqlServerSubscriptionTestBase
        {
            private long _nextTransactionId = 101;

            public Harness(FhirStorageTestsFixture fixture, ITestOutputHelper output)
                : base(fixture, output)
            {
            }

            public ISubscriptionStore SubscriptionStore => Store;

            public async Task<IReadOnlyList<long>> AddPendingEventsAsync(string subscriptionId, int count)
            {
                // The pump records only transactions after the activation watermark; a fixed low watermark keeps the ids simple.
                ExecuteSql($"UPDATE dbo.SubscriptionStates SET ActivationTransactionId = 100 WHERE SubscriptionId = '{subscriptionId}' AND ActivationTransactionId IS NOT NULL");
                long first = (await Store.GetStateAsync(subscriptionId, CancellationToken.None)).LastEventNumber + 1;
                List<SubscriptionEventCandidate> candidates = Enumerable.Range(0, count)
                    .Select(i => Candidate(subscriptionId, _nextTransactionId + i, _nextTransactionId + i))
                    .ToList();
                _nextTransactionId += count;

                await Store.EnqueueAsync(candidates, SqlServerSubscriptionStore.CursorId, _nextTransactionId, null, CancellationToken.None);
                return Enumerable.Range(0, count).Select(i => first + i).ToList();
            }
        }
    }
}
