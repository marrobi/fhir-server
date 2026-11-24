// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnsureThat;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization
{
    /// <summary>
    /// The server-owned authorization snapshot of a subscription (ADR-2611 "activation grant").
    /// It is captured when an authorized caller creates or updates a subscription through the API, and is bound to the
    /// subscription's content hash, so any change of topic, filters, channel, endpoint, headers or payload needs a new grant.
    /// </summary>
    public sealed class SubscriptionGrant
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// Gets the content hash (<see cref="Models.SubscriptionInfo.ComputeContentHash"/>) the grant is bound to.
        /// </summary>
        public string ContentHash { get; init; }

        /// <summary>
        /// Gets a value indicating whether security was enabled when the grant was captured.
        /// </summary>
        public bool SecurityEnabled { get; init; }

        /// <summary>
        /// Gets a value indicating whether role-based authorization was enabled when the grant was captured.
        /// </summary>
        public bool RoleAuthorizationEnabled { get; init; }

        public string Subject { get; init; }

        public IReadOnlyList<string> Roles { get; init; } = [];

        /// <summary>
        /// Gets a value indicating whether SMART scopes restricted the caller.
        /// </summary>
        public bool Smart { get; init; }

        public IReadOnlyList<SubscriptionGrantScope> Scopes { get; init; } = [];

        public string CompartmentResourceType { get; init; }

        public string CompartmentId { get; init; }

        /// <summary>
        /// Gets the base URL of the request that created the subscription. Used to build absolute references in notifications.
        /// </summary>
        public string RequestBase { get; init; }

        public DateTimeOffset CreatedUtc { get; init; }

        public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

        public static SubscriptionGrant Deserialize(string json)
        {
            EnsureArg.IsNotNullOrWhiteSpace(json, nameof(json));
            return JsonSerializer.Deserialize<SubscriptionGrant>(json, SerializerOptions);
        }
    }
}
