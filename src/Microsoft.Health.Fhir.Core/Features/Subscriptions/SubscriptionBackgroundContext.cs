// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using EnsureThat;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Fhir.Core.Features.Context;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Creates the request context for subscription work that is not done on behalf of an HTTP request: the event pump,
    /// delivery, and registry loads. Such work runs as the system, so it is not filtered by any caller's SMART scopes.
    /// </summary>
    public static class SubscriptionBackgroundContext
    {
        private const string LocalUri = "https://localhost/";

        /// <summary>
        /// Creates a background request context.
        /// </summary>
        /// <param name="method">The name of the work, logged as the request method.</param>
        /// <param name="correlationId">The correlation id, or null to use the current activity or a new id.</param>
        /// <returns>The request context.</returns>
        public static FhirRequestContext Create(string method, string correlationId = null)
        {
            EnsureArg.IsNotNullOrWhiteSpace(method, nameof(method));

            return new FhirRequestContext(
                method: method,
                uriString: LocalUri,
                baseUriString: LocalUri,
                correlationId: correlationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString(),
                requestHeaders: new Dictionary<string, StringValues>(),
                responseHeaders: new Dictionary<string, StringValues>())
            {
                IsBackgroundTask = true,
            };
        }
    }
}
