// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Subscriptions;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.SqlServer.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SqlServerSubscriptionStoreTests
    {
        [Theory]
        [InlineData(null, SubscriptionStoreAvailability.NotReady)]
        [InlineData(SchemaVersionConstants.Subscriptions - 1, SubscriptionStoreAvailability.NotReady)]
        [InlineData(SchemaVersionConstants.Subscriptions, SubscriptionStoreAvailability.Available)]
        public void GivenTheCurrentSchemaVersion_WhenAvailabilityIsRead_ThenTheStoreIsOnlyAvailableOnceTheSubscriptionSchemaExists(int? current, SubscriptionStoreAvailability expected)
        {
            // During a rolling upgrade the server runs before the schema with the subscription procedures is applied.
            var schema = new SchemaInformation(SchemaVersionConstants.Min, SchemaVersionConstants.Max) { Current = current };
            var store = new SqlServerSubscriptionStore(Substitute.For<ISqlRetryService>(), Substitute.For<ISqlServerFhirModel>(), schema, NullLogger<SqlServerSubscriptionStore>.Instance);

            Assert.Equal(expected, store.Availability);
        }
    }
}
