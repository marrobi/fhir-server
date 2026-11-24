// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading.Tasks;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Subscriptions
{
    public class SubscriptionsTestFixture : HttpIntegrationTestFixture<StartupWithSubscriptions>
    {
        public SubscriptionsTestFixture(DataStore dataStore, Format format, TestFhirServerFactory testFhirServerFactory)
            : base(dataStore, format, testFhirServerFactory)
        {
        }

        public WebhookReceiver Webhook { get; } = new();

        protected override Task OnDisposedAsync()
        {
            Webhook.Dispose();
            return Task.CompletedTask;
        }
    }
}
