// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Loads stored <c>Subscription</c> and topic resources for the registry.
    /// </summary>
    public interface ISubscriptionResourceLoader
    {
        Task<IReadOnlyList<ResourceElement>> GetSubscriptionsAsync(CancellationToken cancellationToken);

        Task<IReadOnlyList<ResourceElement>> GetTopicsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Reads the latest version of a subscription, or null when it does not exist or is deleted.
        /// </summary>
        Task<ResourceElement> GetSubscriptionAsync(string id, CancellationToken cancellationToken);

        /// <summary>
        /// Reads the latest version of any resource, or null when it does not exist or is deleted.
        /// </summary>
        Task<ResourceElement> GetResourceAsync(string resourceType, string id, CancellationToken cancellationToken);

        /// <summary>
        /// Reads specific resource versions in one call. Versions that are no longer available, or deleted, are missing from the result.
        /// </summary>
        Task<IReadOnlyDictionary<ResourceKey, ResourceElement>> GetResourceVersionsAsync(IReadOnlyList<ResourceKey> keys, CancellationToken cancellationToken);
    }
}
