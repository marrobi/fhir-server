// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Extensions.Configuration;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Subscriptions
{
    // Isolated so the subscription registry never processes subscriptions written by servers that have the feature off.
    [RequiresSubscriptions]
    [RequiresIsolatedDatabase]
    public class StartupWithSubscriptions : StartupBaseForCustomProviders
    {
        public StartupWithSubscriptions(IConfiguration configuration)
            : base(configuration)
        {
        }
    }
}
