// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// Version-independent (R5-shaped) representation of a <c>Subscription</c> resource.
    /// </summary>
    public sealed class SubscriptionInfo
    {
        public const string RestHookChannel = "rest-hook";

        public string Id { get; init; }

        public string VersionId { get; init; }

        public SubscriptionStatusCode Status { get; init; }

        public string TopicCanonical { get; init; }

        public IReadOnlyList<SubscriptionFilter> Filters { get; init; } = [];

        public string ChannelType { get; init; }

        public string Endpoint { get; init; }

        public IReadOnlyList<SubscriptionHeader> Headers { get; init; } = [];

        public int? HeartbeatPeriodSeconds { get; init; }

        public int? TimeoutSeconds { get; init; }

        /// <summary>
        /// Gets the MIME type of notifications, for example <c>application/fhir+json</c>.
        /// </summary>
        public string ContentType { get; init; }

        public SubscriptionContentLevel Content { get; init; } = SubscriptionContentLevel.IdOnly;

        public int? MaxCount { get; init; }

        public DateTimeOffset? End { get; init; }

        public string Error { get; init; }

        /// <summary>
        /// Computes a hash of every element that decides what is sent, and where. Status, error, end and heartbeat are excluded,
        /// so server bookkeeping writes do not change it, while any change of topic, filters, channel, endpoint, headers or payload does.
        /// </summary>
        /// <returns>A hex-encoded SHA-256 hash.</returns>
        public string ComputeContentHash()
        {
            var builder = new StringBuilder();
            Append(builder, "topic", TopicCanonical);
            foreach (SubscriptionFilter filter in Filters.OrderBy(f => f.ResourceType, StringComparer.Ordinal).ThenBy(f => f.QueryKey, StringComparer.Ordinal).ThenBy(f => f.QueryValue, StringComparer.Ordinal))
            {
                Append(builder, "filter", $"{filter.ResourceType}?{filter.QueryKey}={filter.QueryValue}");
            }

            Append(builder, "channel", ChannelType);
            Append(builder, "endpoint", Endpoint);
            foreach (SubscriptionHeader header in Headers.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Value, StringComparer.Ordinal))
            {
                Append(builder, "header", header.Name + ":" + header.Value);
            }

            Append(builder, "contentType", ContentType);
            Append(builder, "content", Content.ToString());

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));

            static void Append(StringBuilder sb, string name, string value)
            {
                // Length-prefixing prevents ambiguous concatenations.
                value ??= string.Empty;
                sb.Append(name).Append('#').Append(value.Length).Append(':').Append(value).Append('\n');
            }
        }
    }
}
