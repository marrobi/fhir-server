// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Tests.Common
{
    /// <summary>
    /// A topic resource trigger for <see cref="SubscriptionResources"/>; unset criteria are omitted.
    /// </summary>
    /// <param name="Resource">The resource type the trigger applies to.</param>
    /// <param name="Interactions">The supported interactions: <c>create</c>, <c>update</c> and/or <c>delete</c>.</param>
    public sealed record TopicTrigger(string Resource, string[] Interactions)
    {
        /// <summary>Gets the <c>queryCriteria.previous</c> search.</summary>
        public string Previous { get; init; }

        /// <summary>Gets <c>queryCriteria.resultForCreate</c>.</summary>
        public string ResultForCreate { get; init; }

        /// <summary>Gets the <c>queryCriteria.current</c> search.</summary>
        public string Current { get; init; }

        /// <summary>Gets <c>queryCriteria.resultForDelete</c>.</summary>
        public string ResultForDelete { get; init; }

        /// <summary>Gets <c>queryCriteria.requireBoth</c>.</summary>
        public bool? RequireBoth { get; init; }

        /// <summary>Gets the <c>fhirPathCriteria</c>.</summary>
        public string FhirPathCriteria { get; init; }

        internal bool HasQueryCriteria => Previous != null || Current != null || ResultForCreate != null || ResultForDelete != null || RequireBoth.HasValue;
    }
}
