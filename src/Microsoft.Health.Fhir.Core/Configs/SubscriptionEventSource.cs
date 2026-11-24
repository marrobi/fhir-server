// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Configs
{
    /// <summary>
    /// The committed-change source that the subscription event pump reads (ADR-2611).
    /// </summary>
    public enum SubscriptionEventSource
    {
        /// <summary>
        /// Reads <c>dbo.Transactions</c> up to the visibility watermark. Covers every write path, including <c>$import</c>.
        /// </summary>
        Transactions = 0,

        /// <summary>
        /// Reads the <c>ResourceChangeData</c> change feed. Requires <c>CoreFeatures:SupportsResourceChangeCapture</c>, and covers
        /// only the writes the change feed records. Its reader takes a table lock that can block concurrent writes.
        /// </summary>
        ResourceChangeData = 1,
    }
}
