// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// The result of matching a resource change to a subscription.
    /// </summary>
    public enum MatchOutcome
    {
        NoMatch,
        Match,

        /// <summary>
        /// A resource version needed to decide was unavailable.
        /// </summary>
        Unevaluable,
    }
}
