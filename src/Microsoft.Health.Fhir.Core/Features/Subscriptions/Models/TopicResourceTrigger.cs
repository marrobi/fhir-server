// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// A resource trigger of a subscription topic.
    /// </summary>
    public sealed class TopicResourceTrigger
    {
        public string Description { get; init; }

        /// <summary>
        /// Gets the resource type the trigger applies to. May be a base type such as <c>Resource</c> or <c>DomainResource</c>.
        /// </summary>
        public string Resource { get; init; }

        /// <summary>
        /// Gets the interactions that fire the trigger. Empty means every interaction.
        /// </summary>
        public IReadOnlyList<SubscriptionInteraction> SupportedInteractions { get; init; } = [];

        public TopicQueryCriteria QueryCriteria { get; init; }

        public string FhirPathCriteria { get; init; }
    }
}
