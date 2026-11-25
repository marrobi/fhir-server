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
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// In-memory resource storage acting as both the subscription resource loader and the status writer.
    /// </summary>
    internal sealed class InMemorySubscriptionResources : ISubscriptionResourceLoader, ISubscriptionStatusWriter
    {
        private readonly ISubscriptionModelConverter _converter;

        public InMemorySubscriptionResources(ISubscriptionModelConverter converter)
        {
            _converter = converter;
        }

        public ConcurrentDictionary<(string Type, string Id), ResourceElement> Resources { get; } = new();

        public ConcurrentDictionary<(string Type, string Id, int Version), ResourceElement> Versions { get; } = new();

        public List<(string Id, SubscriptionStatusCode Status, string Error)> StatusWrites { get; } = [];

        public void Put(ResourceElement resource, DateTimeOffset? lastUpdated = null)
        {
            Resource poco = resource.ToPoco<Resource>();
            int version = Resources.TryGetValue((poco.TypeName, poco.Id), out ResourceElement existing) ? int.Parse(existing.VersionId, CultureInfo.InvariantCulture) + 1 : 1;
            poco.Meta ??= new Meta();
            poco.Meta.VersionId = version.ToString(CultureInfo.InvariantCulture);
            poco.Meta.LastUpdated = lastUpdated ?? Clock.UtcNow;
            ResourceElement stored = poco.ToResourceElement();
            Resources[(poco.TypeName, poco.Id)] = stored;
            Versions[(poco.TypeName, poco.Id, version)] = stored;
        }

        public Task<IReadOnlyList<ResourceElement>> GetSubscriptionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceElement>>(Resources.Where(r => r.Key.Type == "Subscription").Select(r => r.Value).ToList());

        public Task<IReadOnlyList<ResourceElement>> GetTopicsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceElement>>(Resources.Where(r => r.Key.Type == _converter.TopicResourceType).Select(r => r.Value).ToList());

        public Task<ResourceElement> GetSubscriptionAsync(string id, CancellationToken cancellationToken) => GetResourceAsync("Subscription", id, cancellationToken);

        public Task<ResourceElement> GetResourceAsync(string resourceType, string id, CancellationToken cancellationToken) =>
            Task.FromResult(Resources.GetValueOrDefault((resourceType, id)));

        public Task<IReadOnlyDictionary<ResourceKey, ResourceElement>> GetResourceVersionsAsync(IReadOnlyList<ResourceKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ResourceKey, ResourceElement>>(keys
                .Where(k => Versions.ContainsKey((k.ResourceType, k.Id, int.Parse(k.VersionId, CultureInfo.InvariantCulture))))
                .ToDictionary(k => k, k => Versions[(k.ResourceType, k.Id, int.Parse(k.VersionId, CultureInfo.InvariantCulture))]));

        public Task<bool> SetStatusAsync(string subscriptionId, string expectedVersionId, SubscriptionStatusCode status, string error, CancellationToken cancellationToken)
        {
            if (!Resources.TryGetValue(("Subscription", subscriptionId), out ResourceElement current) || (expectedVersionId != null && current.VersionId != expectedVersionId))
            {
                return Task.FromResult(false);
            }

            Put(_converter.WithStatus(current, status, error));
            StatusWrites.Add((subscriptionId, status, error));
            return Task.FromResult(true);
        }
    }
}
