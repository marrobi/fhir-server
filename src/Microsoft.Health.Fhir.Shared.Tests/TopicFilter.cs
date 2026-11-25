// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Tests.Common
{
    /// <summary>
    /// A topic <c>canFilterBy</c> entry for <see cref="SubscriptionResources"/>.
    /// </summary>
    /// <param name="Resource">The resource type the filter applies to.</param>
    /// <param name="Parameter">The search parameter subscriptions may filter on.</param>
    public sealed record TopicFilter(string Resource, string Parameter)
    {
        /// <summary>Gets the allowed modifiers; empty allows any.</summary>
        public string[] Modifiers { get; init; } = [];

        /// <summary>Gets the allowed comparators (R4 and R5 only); empty allows any.</summary>
        public string[] Comparators { get; init; } = [];

        public static implicit operator TopicFilter((string Resource, string Parameter) filter) => new(filter.Resource, filter.Parameter);
    }
}
