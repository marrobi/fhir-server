// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// A channel that records payloads and returns scripted results.
    /// </summary>
    internal sealed class RecordingChannel : ISubscriptionChannel
    {
        public string ChannelType => "rest-hook";

        public List<string> Payloads { get; } = [];

        public Queue<ChannelDeliveryResult> Results { get; } = new();

        public Task<ChannelDeliveryResult> SendAsync(SubscriptionInfo subscription, string payload, CancellationToken cancellationToken)
        {
            Payloads.Add(payload);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : ChannelDeliveryResult.Success);
        }
    }
}
