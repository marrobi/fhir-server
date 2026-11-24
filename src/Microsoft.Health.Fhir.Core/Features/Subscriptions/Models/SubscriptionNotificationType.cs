// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// The type of a subscription notification bundle.
    /// </summary>
    public enum SubscriptionNotificationType
    {
        Handshake,
        Heartbeat,
        EventNotification,
        QueryStatus,
    }
}
