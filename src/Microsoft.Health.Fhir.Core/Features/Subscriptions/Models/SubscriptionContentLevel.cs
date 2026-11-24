// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// The amount of resource content included in notifications.
    /// </summary>
    public enum SubscriptionContentLevel
    {
        Empty,
        IdOnly,
        FullResource,
    }
}
