// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// A filter requested by a subscription (R5 <c>Subscription.filterBy</c>, or the Backport <c>backport-filter-criteria</c> extension).
    /// </summary>
    public sealed class SubscriptionFilter
    {
        /// <summary>
        /// Gets the resource type the filter applies to. Null means every resource type the topic covers.
        /// </summary>
        public string ResourceType { get; init; }

        public string FilterParameter { get; init; }

        /// <summary>
        /// Gets the search comparator prefix (for example <c>gt</c>), or null for equality.
        /// </summary>
        public string Comparator { get; init; }

        /// <summary>
        /// Gets the search modifier (for example <c>not</c>), or null.
        /// </summary>
        public string Modifier { get; init; }

        public string Value { get; init; }

        /// <summary>
        /// Gets the search parameter key, including any modifier (for example <c>status:not</c>).
        /// </summary>
        public string QueryKey => string.IsNullOrEmpty(Modifier) ? FilterParameter : FilterParameter + ":" + Modifier;

        /// <summary>
        /// Gets the search value, including any comparator prefix.
        /// </summary>
        public string QueryValue => string.IsNullOrEmpty(Comparator) || string.Equals(Comparator, "eq", StringComparison.Ordinal) ? Value : Comparator + Value;
    }
}
