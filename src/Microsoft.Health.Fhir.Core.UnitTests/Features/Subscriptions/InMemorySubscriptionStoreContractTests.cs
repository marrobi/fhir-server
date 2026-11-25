// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public sealed class InMemorySubscriptionStoreContractTests : SubscriptionStoreContractTests
    {
        private readonly InMemorySubscriptionStore _store = new();

        protected override ISubscriptionStore Store => _store;

        protected override Task<IReadOnlyList<long>> AddPendingEventsAsync(string subscriptionId, int count)
        {
            long first = _store.States[subscriptionId].LastEventNumber + 1;
            for (int i = 0; i < count; i++)
            {
                _store.AddEvent(subscriptionId, "Encounter", $"e{first + i}", 1);
            }

            return Task.FromResult<IReadOnlyList<long>>(Enumerable.Range(0, count).Select(i => first + i).ToList());
        }
    }
}
