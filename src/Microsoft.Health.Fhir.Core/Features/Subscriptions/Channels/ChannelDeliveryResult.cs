// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels
{
    /// <summary>
    /// The result of sending a notification. Success means the endpoint accepted it (R5 3.9.9); it does not mean the
    /// subscriber processed it.
    /// </summary>
    public sealed record ChannelDeliveryResult(bool Succeeded, string Error)
    {
        public static ChannelDeliveryResult Success { get; } = new(true, null);

        public static ChannelDeliveryResult Failure(string error) => new(false, error);
    }
}
