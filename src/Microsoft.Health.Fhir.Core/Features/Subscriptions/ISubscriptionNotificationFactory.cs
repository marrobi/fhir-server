// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Builds version-specific notification and status bundles.
    /// </summary>
    public interface ISubscriptionNotificationFactory
    {
        /// <summary>
        /// Builds a notification bundle (R4/R4B: <c>history</c>; R5: <c>subscription-notification</c>).
        /// </summary>
        /// <param name="request">The notification content.</param>
        /// <returns>The bundle.</returns>
        ResourceElement CreateNotification(SubscriptionNotificationRequest request);

        /// <summary>
        /// Builds the $status result: a <c>searchset</c> bundle with one status resource per subscription
        /// (R4: <c>Parameters</c>; R4B/R5: <c>SubscriptionStatus</c>).
        /// </summary>
        /// <param name="statuses">The status content of each subscription, with type <c>query-status</c>.</param>
        /// <param name="selfUrl">The request URL, used as the bundle's self link.</param>
        /// <returns>The bundle.</returns>
        ResourceElement CreateStatusBundle(IReadOnlyList<SubscriptionNotificationRequest> statuses, Uri selfUrl = null);

        /// <summary>
        /// Serializes a resource for delivery in the requested MIME type.
        /// </summary>
        /// <param name="resource">The resource.</param>
        /// <param name="contentType">The subscription content type.</param>
        /// <returns>The serialized payload.</returns>
        string Serialize(ResourceElement resource, string contentType);
    }
}
