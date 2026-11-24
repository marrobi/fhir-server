// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Thrown when a <c>Subscription</c> or subscription topic is not acceptable to this server.
    /// </summary>
    public sealed class SubscriptionValidationException : Exception
    {
        public SubscriptionValidationException(string error)
            : this([error])
        {
        }

        public SubscriptionValidationException(IReadOnlyList<string> errors)
            : base(string.Join(" ", errors ?? []))
        {
            Errors = errors?.ToList() ?? [];
        }

        public IReadOnlyList<string> Errors { get; }
    }
}
