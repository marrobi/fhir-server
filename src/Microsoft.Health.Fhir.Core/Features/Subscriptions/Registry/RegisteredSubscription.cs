// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry
{
    /// <summary>
    /// A stored subscription, revalidated, with its topic, state and grant.
    /// </summary>
    public sealed class RegisteredSubscription
    {
        public string Id { get; init; }

        /// <summary>
        /// Gets the parsed subscription, or null when the resource could not be parsed.
        /// </summary>
        public SubscriptionInfo Info { get; init; }

        public SubscriptionTopicInfo Topic { get; init; }

        public SubscriptionState State { get; init; }

        /// <summary>
        /// Gets the grant, when one exists and is bound to the subscription's current content.
        /// </summary>
        public SubscriptionGrant Grant { get; init; }

        public IReadOnlyList<string> Errors { get; init; } = [];

        /// <summary>
        /// Gets the stored <c>status</c> code, which is available even when the resource could not be parsed.
        /// </summary>
        public string RawStatus { get; init; }

        public bool IsValid => Errors.Count == 0 && Info != null;

        public bool HasValidGrant => Grant != null;

        /// <summary>
        /// Gets a value indicating whether resource changes should be matched for this subscription: it is valid,
        /// authorized, activated, and either active or in error (events are retained while in error).
        /// </summary>
        public bool IsMatchable =>
            IsValid && HasValidGrant && Topic != null && State?.ActivationTransactionId != null &&
            (Info.Status == SubscriptionStatusCode.Active || Info.Status == SubscriptionStatusCode.Error);
    }
}
