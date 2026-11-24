// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// The processing state of a subscription event in the outbox.
    /// </summary>
    public enum SubscriptionEventStatus
    {
        /// <summary>Matched and waiting to be delivered.</summary>
        Pending = 0,

        /// <summary>Sent and acknowledged by the endpoint.</summary>
        Delivered = 1,

        /// <summary>The changed or previous version was unavailable, so the event could not be evaluated. It keeps its event number but is never sent.</summary>
        Unevaluable = 2,

        /// <summary>Authorization no longer covered the event at delivery time, so it was not sent.</summary>
        NotAuthorized = 3,
    }
}
