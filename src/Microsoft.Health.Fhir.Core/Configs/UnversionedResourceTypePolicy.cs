// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Configs
{
    /// <summary>
    /// How subscriptions treat resource types whose versioning policy is <c>no-version</c>, where an earlier version is
    /// overwritten by the next write and may be gone before the event pump reads it (ADR-2611).
    /// </summary>
    public enum UnversionedResourceTypePolicy
    {
        /// <summary>
        /// Topics may trigger on these types. A version that is gone before it is matched is recorded as unevaluable, so
        /// subscribers see a gap in event numbers instead of a notification.
        /// </summary>
        AllowWithGaps = 0,

        /// <summary>
        /// Topics that trigger on these types are rejected, so every notification is matched against the exact version written.
        /// </summary>
        RequireHistory = 1,
    }
}
