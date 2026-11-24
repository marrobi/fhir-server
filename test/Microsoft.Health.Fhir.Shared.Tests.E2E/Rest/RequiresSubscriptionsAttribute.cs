// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest
{
    /// <summary>
    /// An attribute to be placed on a custom <see cref="Startup"/> type for in-proc E2E tests, when the in-proc server must run
    /// with topic-based subscriptions enabled and notifications allowed to a local HTTP receiver.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RequiresSubscriptionsAttribute : Attribute
    {
    }
}
