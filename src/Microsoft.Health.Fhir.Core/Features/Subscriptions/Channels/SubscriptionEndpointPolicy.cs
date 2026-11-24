// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels
{
    /// <summary>
    /// Server policy for notification endpoints: HTTPS, an optional host allow-list, and no loopback, private, link-local
    /// or other non-public addresses. Literal addresses are checked when a subscription is written, and every resolved
    /// address is checked again when a connection is opened, which also defeats DNS rebinding.
    /// </summary>
    public sealed class SubscriptionEndpointPolicy
    {
        private readonly SubscriptionsConfiguration _configuration;

        public SubscriptionEndpointPolicy(IOptions<SubscriptionsConfiguration> configuration)
        {
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
        }

        /// <summary>
        /// Validates an endpoint URL.
        /// </summary>
        /// <param name="endpoint">The endpoint.</param>
        /// <param name="error">The reason it is rejected, or null.</param>
        /// <returns>Whether the endpoint is allowed.</returns>
        public bool TryValidate(string endpoint, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
            {
                error = Core.Resources.SubscriptionEndpointNotAbsolute;
                return false;
            }

            bool schemeAllowed = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && _configuration.AllowHttpEndpoints);
            if (!schemeAllowed)
            {
                error = Core.Resources.SubscriptionEndpointMustBeHttps;
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                error = Core.Resources.SubscriptionEndpointUserInfo;
                return false;
            }

            if (_configuration.AllowedEndpointHosts.Count > 0 && !_configuration.AllowedEndpointHosts.Any(h => HostMatches(uri.IdnHost, h)))
            {
                error = string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointHostNotAllowed, uri.Host);
                return false;
            }

            if (!_configuration.AllowPrivateNetworkEndpoints)
            {
                if (string.Equals(uri.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                {
                    error = string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointAddressNotAllowed, uri.Host);
                    return false;
                }

                if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out IPAddress address) && IsNonPublic(address))
                {
                    error = string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointAddressNotAllowed, uri.Host);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when a resolved address may be connected to.
        /// </summary>
        /// <param name="address">The address.</param>
        /// <returns>Whether connecting is allowed.</returns>
        public bool IsAddressAllowed(IPAddress address)
        {
            EnsureArg.IsNotNull(address, nameof(address));
            return _configuration.AllowPrivateNetworkEndpoints || !IsNonPublic(address);
        }

        /// <summary>
        /// Returns true for loopback, private, link-local, carrier-grade NAT, unspecified, multicast and other non-public addresses.
        /// </summary>
        /// <param name="address">The address.</param>
        /// <returns>Whether the address is not publicly routable.</returns>
        public static bool IsNonPublic(IPAddress address)
        {
            EnsureArg.IsNotNull(address, nameof(address));

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = address.GetAddressBytes();
                return b[0] == 0 // "this" network
                    || b[0] == 10 // private
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
                    || b[0] == 127 // loopback
                    || (b[0] == 169 && b[1] == 254) // link-local, including cloud metadata endpoints
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // private
                    || (b[0] == 192 && b[1] == 0 && b[2] == 0) // IETF protocol assignments
                    || (b[0] == 192 && b[1] == 168) // private
                    || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
                    || b[0] >= 224; // multicast and reserved
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                byte[] b = address.GetAddressBytes();
                return address.Equals(IPAddress.IPv6None)
                    || address.IsIPv6LinkLocal
                    || address.IsIPv6SiteLocal
                    || address.IsIPv6Multicast
                    || (b[0] & 0xFE) == 0xFC; // unique local fc00::/7
            }

            return true;
        }

        private static bool HostMatches(string host, string allowed)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                return false;
            }

            if (allowed.StartsWith("*.", StringComparison.Ordinal))
            {
                return host.EndsWith(allowed.Substring(1), StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase);
        }
    }
}
