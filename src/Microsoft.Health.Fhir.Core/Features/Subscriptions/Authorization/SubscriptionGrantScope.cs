// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Security;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization
{
    /// <summary>
    /// A SMART scope captured in a subscription grant.
    /// </summary>
    public sealed class SubscriptionGrantScope
    {
        /// <summary>
        /// Gets the resource type of the scope, or <c>all</c>.
        /// </summary>
        public string Resource { get; init; }

        public DataActions DataActions { get; init; }

        /// <summary>
        /// Gets the SMART v2 fine-grained search restriction of the scope, as query parameters. Empty when unrestricted.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> SearchParameters { get; init; } = [];
    }
}
