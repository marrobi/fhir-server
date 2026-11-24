// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry
{
    /// <summary>
    /// An immutable view of all subscriptions and topics at a point in time.
    /// </summary>
    public sealed class SubscriptionRegistrySnapshot
    {
        public SubscriptionRegistrySnapshot(IReadOnlyList<RegisteredSubscription> subscriptions, IReadOnlyDictionary<string, SubscriptionTopicInfo> topics, DateTimeOffset loadedUtc)
        {
            Subscriptions = subscriptions;
            Topics = topics;
            LoadedUtc = loadedUtc;
            ById = subscriptions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        }

        public static SubscriptionRegistrySnapshot Empty { get; } = new([], new Dictionary<string, SubscriptionTopicInfo>(), DateTimeOffset.MinValue);

        public IReadOnlyList<RegisteredSubscription> Subscriptions { get; }

        public IReadOnlyDictionary<string, RegisteredSubscription> ById { get; }

        /// <summary>
        /// Gets topics keyed by canonical and by <c>canonical|version</c>.
        /// </summary>
        public IReadOnlyDictionary<string, SubscriptionTopicInfo> Topics { get; }

        public DateTimeOffset LoadedUtc { get; }
    }
}
