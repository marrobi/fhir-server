// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels
{
    /// <summary>
    /// Sends serialized notifications over a channel type.
    /// </summary>
    public interface ISubscriptionChannel
    {
        /// <summary>
        /// Gets the channel type code, for example <c>rest-hook</c>.
        /// </summary>
        string ChannelType { get; }

        /// <summary>
        /// Sends a notification.
        /// </summary>
        /// <returns>The delivery result.</returns>
        Task<ChannelDeliveryResult> SendAsync(SubscriptionInfo subscription, string payload, CancellationToken cancellationToken);
    }
}
