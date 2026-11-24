// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// The result to use for a query criterion when the version it applies to does not exist (create or delete).
    /// </summary>
    public enum CriteriaNotExistsBehavior
    {
        TestPasses,
        TestFails,
    }
}
