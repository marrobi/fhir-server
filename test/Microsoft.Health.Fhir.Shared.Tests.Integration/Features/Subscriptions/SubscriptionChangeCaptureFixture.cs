// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.Tests.Integration.Persistence;
using Xunit;

namespace Microsoft.Health.Fhir.Tests.Integration.Features.Subscriptions
{
    /// <summary>
    /// A SQL Server database with resource change capture enabled, so the <c>ResourceChangeData</c> change feed is recorded.
    /// </summary>
    public sealed class SubscriptionChangeCaptureFixture : IAsyncLifetime
    {
        public SubscriptionChangeCaptureFixture()
        {
            var sql = new SqlServerFhirStorageTestsFixture(
                SchemaVersionConstants.Max,
                SqlServerFhirStorageTestsFixture.GetDatabaseName("SubscriptionsChangeFeed"),
                Options.Create(new CoreFeatureConfiguration { SupportsResourceChangeCapture = true }));
            Storage = new FhirStorageTestsFixture(sql);
        }

        public FhirStorageTestsFixture Storage { get; }

        public ValueTask InitializeAsync() => Storage.InitializeAsync();

        public ValueTask DisposeAsync() => Storage.DisposeAsync();
    }
}
