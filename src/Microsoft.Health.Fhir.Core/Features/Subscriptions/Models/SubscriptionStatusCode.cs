// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// The lifecycle status of a subscription.
    /// </summary>
    public enum SubscriptionStatusCode
    {
        Requested,
        Active,
        Error,
        Off,
        EnteredInError,
    }
}
