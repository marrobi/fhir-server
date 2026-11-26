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
        [InlineData("https://168.63.129.16/machine", false)]
        [InlineData("https://168.63.129.17/hook", true)]
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

        [Theory]
        [InlineData("10.20.1.5", true)] // inside the listed range
        [InlineData("10.21.0.1", false)] // private, but not listed
        [InlineData("192.168.1.1", false)]
        [InlineData("::ffff:10.20.1.5", true)] // IPv4-mapped form of a listed address
        [InlineData("2002:0a14:0105::1", true)] // 6to4 of 10.20.1.5
        [InlineData("fd00:1::5", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("::1", false)] // IPv6 loopback is not covered by 127.0.0.0/8
        [InlineData("8.8.8.8", true)] // public addresses stay allowed
        public void GivenAllowedPrivateNetworks_WhenCheckingAnAddress_ThenOnlyListedPrivateRangesAreAllowed(string address, bool expected)
        {
            SubscriptionEndpointPolicy policy = PolicyAllowing("10.20.0.0/16", "fd00:1::/64", "127.0.0.0/8");

            Assert.Equal(expected, policy.IsAddressAllowed(IPAddress.Parse(address)));
        }

        [Theory]
        [InlineData("169.254.169.254")] // cloud metadata
        [InlineData("168.63.129.16")] // Azure platform
        [InlineData("fe80::1")] // IPv6 link-local
        [InlineData("::ffff:169.254.169.254")]
        [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of 169.254.169.254
        [InlineData("0.0.0.0")]
        [InlineData("224.0.0.1")]
        public void GivenAllowedPrivateNetworksCoveringEverything_WhenCheckingAProtectedAddress_ThenItIsStillBlocked(string address)
        {
            SubscriptionEndpointPolicy policy = PolicyAllowing("0.0.0.0/0", "::/0");

            Assert.False(policy.IsAddressAllowed(IPAddress.Parse(address)));
            Assert.False(policy.TryValidate($"https://{(address.Contains(':', System.StringComparison.Ordinal) ? $"[{address}]" : address)}/hook", out _));
        }

        [Theory]
        [InlineData("127.0.0.0/8", true)]
        [InlineData("::1/128", true)]
        [InlineData("10.0.0.0/8", false)]
        public void GivenALocalhostEndpoint_WhenValidating_ThenItIsAllowedOnlyWhenLoopbackIsListed(string network, bool expected)
        {
            Assert.Equal(expected, PolicyAllowing(network).TryValidate("https://localhost/hook", out _));
        }

        [Fact]
        public void GivenAnInvalidAllowedPrivateNetwork_WhenThePolicyIsCreated_ThenItFailsWithTheValue()
        {
            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(() => PolicyAllowing("10.0.0.0/33"));

            Assert.Contains("10.0.0.0/33", ex.Message, System.StringComparison.Ordinal);
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
        [InlineData("64:ff9b::a9fe:a9fe", true)] // NAT64 of 169.254.169.254
        [InlineData("64:ff9b::808:808", false)] // NAT64 of 8.8.8.8
        [InlineData("64:ff9b:1::808:808", true)] // local-use NAT64
        [InlineData("2002:0a00:0001::1", true)] // 6to4 of 10.0.0.1
        [InlineData("2002:0808:0808::1", false)] // 6to4 of 8.8.8.8
        [InlineData("::a9fe:a9fe", true)] // IPv4-compatible 169.254.169.254
        [InlineData("::", true)]
        public void GivenAnAddress_WhenCheckingIfNonPublic_ThenReservedRangesAreDetected(string address, bool expected)
        {
            Assert.Equal(expected, SubscriptionEndpointPolicy.IsNonPublic(IPAddress.Parse(address)));
        }

        private static SubscriptionEndpointPolicy PolicyAllowing(params string[] networks)
        {
            var configuration = new SubscriptionsConfiguration();
            foreach (string network in networks)
            {
                configuration.AllowedPrivateNetworks.Add(network);
            }

            return new SubscriptionEndpointPolicy(Options.Create(configuration));
        }
    }
}
