// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Loads stored subscription and topic resources through the search service and data store.
    /// </summary>
    public sealed class SubscriptionResourceLoader : ISubscriptionResourceLoader
    {
        private const int PageSize = 500;

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IResourceDeserializer _deserializer;
        private readonly ISubscriptionModelConverter _converter;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;

        public SubscriptionResourceLoader(IServiceScopeFactory scopeFactory, IResourceDeserializer deserializer, ISubscriptionModelConverter converter, RequestContextAccessor<IFhirRequestContext> requestContextAccessor)
        {
            _scopeFactory = EnsureArg.IsNotNull(scopeFactory, nameof(scopeFactory));
            _deserializer = EnsureArg.IsNotNull(deserializer, nameof(deserializer));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
        }

        public Task<IReadOnlyList<ResourceElement>> GetSubscriptionsAsync(CancellationToken cancellationToken)
        {
            return SearchAllAsync(KnownResourceTypes.Subscription, [], cancellationToken);
        }

        public Task<IReadOnlyList<ResourceElement>> GetTopicsAsync(CancellationToken cancellationToken)
        {
            return _converter.IsSupported
                ? SearchAllAsync(_converter.TopicResourceType, _converter.TopicSearchParameters, cancellationToken)
                : Task.FromResult<IReadOnlyList<ResourceElement>>([]);
        }

        public async Task<ResourceElement> GetSubscriptionAsync(string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(id, nameof(id));
            return await GetAsync(new ResourceKey(KnownResourceTypes.Subscription, id), cancellationToken);
        }

        public async Task<ResourceElement> GetResourceAsync(string resourceType, string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(id, nameof(id));
            return await GetAsync(new ResourceKey(resourceType, id), cancellationToken);
        }

        public async Task<IReadOnlyDictionary<ResourceKey, ResourceElement>> GetResourceVersionsAsync(IReadOnlyList<ResourceKey> keys, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(keys, nameof(keys));
            var result = new Dictionary<ResourceKey, ResourceElement>();
            if (keys.Count == 0)
            {
                return result;
            }

            using IServiceScope scope = _scopeFactory.CreateScope();
            IFhirDataStore dataStore = scope.ServiceProvider.GetRequiredService<IFhirDataStore>();
            foreach (ResourceWrapper wrapper in await dataStore.GetAsync(keys, cancellationToken))
            {
                // A deleted version has no content.
                if (wrapper.RawResource?.Data != null && !wrapper.IsDeleted)
                {
                    result[new ResourceKey(wrapper.ResourceTypeName, wrapper.ResourceId, wrapper.Version)] = _deserializer.Deserialize(wrapper);
                }
            }

            return result;
        }

        private async Task<ResourceElement> GetAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            IFhirDataStore dataStore = scope.ServiceProvider.GetRequiredService<IFhirDataStore>();

            ResourceWrapper wrapper = await dataStore.GetAsync(key, cancellationToken);
            if (wrapper == null || wrapper.RawResource?.Data == null)
            {
                return null;
            }

            // A deleted current version means the resource no longer exists; a specific deleted version has no content.
            return wrapper.IsDeleted ? null : _deserializer.Deserialize(wrapper);
        }

        private async Task<IReadOnlyList<ResourceElement>> SearchAllAsync(string resourceType, IReadOnlyList<Tuple<string, string>> parameters, CancellationToken cancellationToken)
        {
            // The registry is shared by every caller, so it must not be filtered by the SMART scopes or compartment of whichever
            // request happens to trigger a load. Authorization of events uses each subscription's own grant instead.
            IFhirRequestContext callerContext = _requestContextAccessor.RequestContext;
            _requestContextAccessor.RequestContext = SubscriptionBackgroundContext.Create("SubscriptionRegistry", callerContext?.CorrelationId);

            try
            {
                return await SearchAllAsSystemAsync(resourceType, parameters, cancellationToken);
            }
            finally
            {
                _requestContextAccessor.RequestContext = callerContext;
            }
        }

        private async Task<IReadOnlyList<ResourceElement>> SearchAllAsSystemAsync(string resourceType, IReadOnlyList<Tuple<string, string>> parameters, CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            ISearchService searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

            var resources = new List<ResourceElement>();
            string continuationToken = null;
            do
            {
                var query = new List<Tuple<string, string>>(parameters) { Tuple.Create(KnownQueryParameterNames.Count, PageSize.ToString(CultureInfo.InvariantCulture)) };
                if (!string.IsNullOrEmpty(continuationToken))
                {
                    query.Add(Tuple.Create(KnownQueryParameterNames.ContinuationToken, ContinuationTokenEncoder.Encode(continuationToken)));
                }

                SearchResult result = await searchService.SearchAsync(resourceType, query, cancellationToken);
                resources.AddRange(result.Results.Where(r => r.Resource != null).Select(r => _deserializer.Deserialize(r.Resource)));
                continuationToken = result.ContinuationToken;
            }
            while (!string.IsNullOrEmpty(continuationToken));

            return resources;
        }
    }
}
