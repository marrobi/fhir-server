// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Validation.FhirPrimitiveTypes;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionBackgroundContextTests
    {
        private static readonly Uri ServerBase = new("https://fhir.example/");

        [Fact]
        public void GivenABackgroundContextForTheServer_WhenAnAbsoluteServerReferenceIsParsed_ThenItIsInternalAsInAnApiRequest()
        {
            // Arrange: the event pump indexes and matches resources under this context, so a filter such as
            // patient=https://fhir.example/Patient/1 must resolve the same way as on the write path.
            var accessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            accessor.RequestContext.Returns(SubscriptionBackgroundContext.Create("SubscriptionEventWatchdog", ServerBase));
            var instanceConfiguration = Substitute.For<IFhirServerInstanceConfiguration>();
            instanceConfiguration.BaseUri.Returns(ServerBase);
            var parser = new ReferenceSearchValueParser(accessor, instanceConfiguration, ResourceIdPolicy.Standard);

            // Act
            ReferenceSearchValue value = parser.Parse("https://fhir.example/Patient/1");

            // Assert
            Assert.Equal(ReferenceKind.Internal, value.Kind);
            Assert.Null(value.BaseUri);
        }
    }
}
