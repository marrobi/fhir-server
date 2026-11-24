// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// Query-based criteria of a topic resource trigger (R5 <c>SubscriptionTopic.resourceTrigger.queryCriteria</c>).
    /// </summary>
    public sealed class TopicQueryCriteria
    {
        public string Previous { get; init; }

        public CriteriaNotExistsBehavior? ResultForCreate { get; init; }

        public string Current { get; init; }

        public CriteriaNotExistsBehavior? ResultForDelete { get; init; }

        public bool RequireBoth { get; init; }

        public bool IsEmpty => string.IsNullOrWhiteSpace(Previous) && string.IsNullOrWhiteSpace(Current);
    }
}
