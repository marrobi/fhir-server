// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionQueryParserTests
    {
        [Theory]
        [InlineData("status=finished", null, 1)]
        [InlineData("?status=finished", null, 1)]
        [InlineData("Encounter?status=finished&subject=Patient/1", "Encounter", 2)]
        [InlineData("Encounter", "Encounter", 0)]
        [InlineData("", null, 0)]
        public void GivenAQuery_WhenParsed_ThenTheTypeAndParametersAreReturned(string query, string expectedType, int expectedCount)
        {
            IReadOnlyList<KeyValuePair<string, string>> parameters = SubscriptionQueryParser.Parse(query, out string type);

            Assert.Equal(expectedType, type);
            Assert.Equal(expectedCount, parameters.Count);
        }

        [Fact]
        public void GivenEncodedValues_WhenParsed_ThenTheyAreDecodedButPlusIsKept()
        {
            IReadOnlyList<KeyValuePair<string, string>> parameters = SubscriptionQueryParser.Parse("date=ge2020-01-01T00:00:00+01:00&code=a%7Cb", out _);

            Assert.Equal("ge2020-01-01T00:00:00+01:00", parameters[0].Value);
            Assert.Equal("a|b", parameters[1].Value);
        }

        [Fact]
        public void GivenAParameterWithoutValue_WhenParsed_ThenItIsRejected()
        {
            Assert.Throws<FormatException>(() => SubscriptionQueryParser.Parse("a=1&b", out _));
            Assert.Throws<FormatException>(() => SubscriptionQueryParser.Parse("a=1&=x", out _));
        }
    }
}
