// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence
{
    /// <summary>
    /// Whether the subscription store can be used now.
    /// </summary>
    public enum SubscriptionStoreAvailability
    {
        /// <summary>The store is ready.</summary>
        Available = 0,

        /// <summary>The data store does not support subscriptions.</summary>
        NotSupported = 1,

        /// <summary>The store is supported but not ready yet, for example while the database schema is being upgraded.</summary>
        NotReady = 2,
    }
}
