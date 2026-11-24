// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Converts between the FHIR-version-specific resources (R4 Backport, R4B Backport, R5) and the internal model.
    /// </summary>
    public interface ISubscriptionModelConverter
    {
        /// <summary>
        /// Gets the resource type that stores topics: <c>SubscriptionTopic</c>, or <c>Basic</c> on R4.
        /// </summary>
        string TopicResourceType { get; }

        /// <summary>
        /// Gets the search parameters that select topics among resources of <see cref="TopicResourceType"/>.
        /// </summary>
        IReadOnlyList<Tuple<string, string>> TopicSearchParameters { get; }

        /// <summary>
        /// Gets a value indicating whether the server's FHIR version supports topic-based subscriptions.
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        /// Returns true when the resource is a subscription topic.
        /// </summary>
        /// <param name="resource">The resource.</param>
        /// <returns>Whether it is a topic.</returns>
        bool IsTopic(ResourceElement resource);

        /// <summary>
        /// Converts a topic resource.
        /// </summary>
        /// <param name="resource">The topic resource.</param>
        /// <returns>The topic.</returns>
        /// <exception cref="SubscriptionValidationException">The resource is not a valid topic.</exception>
        SubscriptionTopicInfo ToTopic(ResourceElement resource);

        /// <summary>
        /// Converts a <c>Subscription</c> resource.
        /// </summary>
        /// <param name="resource">The subscription resource.</param>
        /// <returns>The subscription.</returns>
        /// <exception cref="SubscriptionValidationException">The resource is not a valid topic-based subscription.</exception>
        SubscriptionInfo ToSubscription(ResourceElement resource);

        /// <summary>
        /// Applies server-owned values to a subscription being written by a client: the status, the default payload content
        /// level when none is given, and the capped end time. The underlying resource instance is modified.
        /// </summary>
        /// <param name="resource">The subscription resource.</param>
        /// <param name="status">The status to store.</param>
        /// <param name="end">The end time to store, or null to leave unchanged.</param>
        void ApplyServerValues(ResourceElement resource, SubscriptionStatusCode status, DateTimeOffset? end);

        /// <summary>
        /// Returns a copy of the subscription with the given status and error, for server bookkeeping writes.
        /// </summary>
        /// <param name="resource">The subscription resource.</param>
        /// <param name="status">The new status.</param>
        /// <param name="error">The error text, or null to clear it.</param>
        /// <returns>The updated resource.</returns>
        ResourceElement WithStatus(ResourceElement resource, SubscriptionStatusCode status, string error);
    }
}
