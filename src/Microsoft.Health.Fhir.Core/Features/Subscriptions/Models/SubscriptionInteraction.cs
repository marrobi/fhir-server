// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// The interaction that produced a resource change.
    /// </summary>
    public enum SubscriptionInteraction
    {
        Create = 0,
        Update = 1,
        Delete = 2,
    }
}
