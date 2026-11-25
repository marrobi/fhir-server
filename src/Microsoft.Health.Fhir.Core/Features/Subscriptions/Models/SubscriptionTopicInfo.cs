// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// Version-independent (R5-shaped) representation of a subscription topic.
    /// </summary>
    public sealed class SubscriptionTopicInfo
    {
        /// <summary>
        /// Gets the logical id of the resource that stores the topic (a <c>SubscriptionTopic</c>, or a <c>Basic</c> on R4).
        /// </summary>
        public string ResourceId { get; init; }

        public string Canonical { get; init; }

        public string Version { get; init; }

        public string Title { get; init; }

        /// <summary>
        /// Gets the publication status (<c>draft</c>, <c>active</c>, <c>retired</c>, <c>unknown</c>).
        /// </summary>
        public string Status { get; init; }

        public IReadOnlyList<TopicResourceTrigger> ResourceTriggers { get; init; } = [];

        public IReadOnlyList<TopicCanFilterBy> CanFilterBy { get; init; } = [];

        /// <summary>
        /// Gets a value indicating whether the topic declares event triggers, which are not supported.
        /// </summary>
        public bool HasEventTriggers { get; init; }

        /// <summary>
        /// Gets the canonical with its version (<c>url|version</c>) when a version is present.
        /// </summary>
        public string VersionedCanonical => string.IsNullOrEmpty(Version) ? Canonical : Canonical + "|" + Version;
    }
}
