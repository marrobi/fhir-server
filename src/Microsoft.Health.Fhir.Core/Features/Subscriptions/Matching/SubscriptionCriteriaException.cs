// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// Thrown when topic criteria or subscription filters cannot be evaluated in memory.
    /// </summary>
    public sealed class SubscriptionCriteriaException : Exception
    {
        public SubscriptionCriteriaException(string message)
            : base(message)
        {
        }

        public SubscriptionCriteriaException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
