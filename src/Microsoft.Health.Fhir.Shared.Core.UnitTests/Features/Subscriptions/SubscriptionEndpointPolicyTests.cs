// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionEndpointPolicyTests
    {
        [Theory]
        [InlineData("https://hooks.example.com/fhir", true)]
        [InlineData("http://hooks.example.com/fhir", false)]
        [InlineData("ftp://hooks.example.com/fhir", false)]
        [InlineData("/relative", false)]
        [InlineData("https://user:pass@hooks.example.com/", false)]
        [InlineData("https://localhost/hook", false)]
        [InlineData("https://127.0.0.1/hook", false)]
        [InlineData("https://10.1.2.3/hook", false)]
        [InlineData("https://172.16.0.1/hook", false)]
        [InlineData("https://192.168.1.1/hook", false)]
        [InlineData("https://169.254.169.254/metadata", false)]
        [InlineData("https://100.64.0.1/hook", false)]
        [InlineData("https://[::1]/hook", false)]
        [InlineData("https://[fd00::1]/hook", false)]
        [InlineData("https://[fe80::1]/hook", false)]
        [InlineData("https://[::ffff:10.0.0.1]/hook", false)]
        [InlineData("https://8.8.8.8/hook", true)]
        public void GivenTheDefaultPolicy_WhenValidatingAnEndpoint_ThenOnlyPublicHttpsIsAllowed(string endpoint, bool expected)
        {
            var policy = new SubscriptionEndpointPolicy(Options.Create(new SubscriptionsConfiguration()));

            bool result = policy.TryValidate(endpoint, out string error);

            Assert.Equal(expected, result);
            Assert.Equal(expected, error == null);
        }

        [Fact]
        public void GivenDevelopmentSettings_WhenValidatingALocalHttpEndpoint_ThenItIsAllowed()
        {
            var policy = new SubscriptionEndpointPolicy(Options.Create(new SubscriptionsConfiguration { AllowHttpEndpoints = true, AllowPrivateNetworkEndpoints = true }));

            Assert.True(policy.TryValidate("http://webhook:8080/notify", out _));
            Assert.True(policy.IsAddressAllowed(IPAddress.Loopback));
        }

        [Theory]
        [InlineData("https://hooks.example.com/x", true)]
        [InlineData("https://a.partner.org/x", true)]
        [InlineData("https://partner.org.evil.com/x", false)]
        [InlineData("https://other.example.com/x", false)]
        public void GivenAnAllowList_WhenValidating_ThenOnlyListedHostsAreAllowed(string endpoint, bool expected)
        {
            var configuration = new SubscriptionsConfiguration();
            configuration.AllowedEndpointHosts.Add("hooks.example.com");
            configuration.AllowedEndpointHosts.Add("*.partner.org");
            var policy = new SubscriptionEndpointPolicy(Options.Create(configuration));

            Assert.Equal(expected, policy.TryValidate(endpoint, out _));
        }

        [Theory]
        [InlineData("8.8.8.8", false)]
        [InlineData("2606:4700:4700::1111", false)]
        [InlineData("0.0.0.0", true)]
        [InlineData("224.0.0.1", true)]
        [InlineData("198.18.0.1", true)]
        public void GivenAnAddress_WhenCheckingIfNonPublic_ThenReservedRangesAreDetected(string address, bool expected)
        {
            Assert.Equal(expected, SubscriptionEndpointPolicy.IsNonPublic(IPAddress.Parse(address)));
        }
    }
}
