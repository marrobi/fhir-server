// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Subscriptions
{
    public sealed record ReceivedNotification(string Path, string ContentType, string Authorization, string Body);
}
