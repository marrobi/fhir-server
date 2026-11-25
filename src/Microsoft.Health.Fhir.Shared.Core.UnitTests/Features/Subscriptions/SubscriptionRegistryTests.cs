// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionRegistryTests : IClassFixture<SubscriptionTestFixture>
    {
        private readonly SubscriptionTestFixture _fixture;

        public SubscriptionRegistryTests(SubscriptionTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task GivenCallersQueuedBehindARefresh_WhenItCompletes_ThenTheyReuseItsSnapshotInsteadOfReloading()
        {
            // Arrange
            var converter = new SubscriptionModelConverter();
            var loader = new GatedLoader(new InMemorySubscriptionResources(converter));
            var registry = new SubscriptionRegistry(loader, converter, new InMemorySubscriptionStore(), _fixture.CreateValidator(new SubscriptionsConfiguration { Enabled = true }), NullLogger<SubscriptionRegistry>.Instance);
            TimeSpan maxAge = TimeSpan.FromHours(1);

            // Act
            Task<SubscriptionRegistrySnapshot> first = registry.GetAsync(maxAge, CancellationToken.None);
            await loader.Entered.Task;
            List<Task<SubscriptionRegistrySnapshot>> queued = Enumerable.Range(0, 5).Select(_ => registry.GetAsync(maxAge, CancellationToken.None)).ToList();
            loader.Release.SetResult();
            SubscriptionRegistrySnapshot[] snapshots = await Task.WhenAll([first, .. queued]);

            // Assert
            Assert.Equal(1, loader.Loads);
            Assert.All(snapshots, s => Assert.Same(snapshots[0], s));
        }

        [Fact]
        public async Task GivenAnInvalidatedSnapshot_WhenACallerWasQueuedBehindTheRefresh_ThenItReloads()
        {
            // Arrange
            var converter = new SubscriptionModelConverter();
            var loader = new GatedLoader(new InMemorySubscriptionResources(converter));
            var registry = new SubscriptionRegistry(loader, converter, new InMemorySubscriptionStore(), _fixture.CreateValidator(new SubscriptionsConfiguration { Enabled = true }), NullLogger<SubscriptionRegistry>.Instance);
            TimeSpan maxAge = TimeSpan.FromHours(1);

            // Act: a write invalidates the registry while a refresh that may not include it is loading.
            Task<SubscriptionRegistrySnapshot> first = registry.GetAsync(maxAge, CancellationToken.None);
            await loader.Entered.Task;
            registry.Invalidate();
            Task<SubscriptionRegistrySnapshot> queued = registry.GetAsync(maxAge, CancellationToken.None);
            loader.Release.SetResult();
            await Task.WhenAll(first, queued);

            // Assert
            Assert.Equal(2, loader.Loads);
        }

        private sealed class GatedLoader : ISubscriptionResourceLoader
        {
            private readonly ISubscriptionResourceLoader _inner;
            private int _loads;

            public GatedLoader(ISubscriptionResourceLoader inner)
            {
                _inner = inner;
            }

            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public int Loads => _loads;

            public async System.Threading.Tasks.Task<IReadOnlyList<ResourceElement>> GetTopicsAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _loads);
                Entered.TrySetResult();
                await Release.Task;
                return await _inner.GetTopicsAsync(cancellationToken);
            }

            public System.Threading.Tasks.Task<IReadOnlyList<ResourceElement>> GetSubscriptionsAsync(CancellationToken cancellationToken) => _inner.GetSubscriptionsAsync(cancellationToken);

            public System.Threading.Tasks.Task<ResourceElement> GetSubscriptionAsync(string id, CancellationToken cancellationToken) => _inner.GetSubscriptionAsync(id, cancellationToken);

            public System.Threading.Tasks.Task<ResourceElement> GetResourceAsync(string resourceType, string id, CancellationToken cancellationToken) => _inner.GetResourceAsync(resourceType, id, cancellationToken);

            public System.Threading.Tasks.Task<IReadOnlyDictionary<ResourceKey, ResourceElement>> GetResourceVersionsAsync(IReadOnlyList<ResourceKey> keys, CancellationToken cancellationToken) => _inner.GetResourceVersionsAsync(keys, cancellationToken);
        }
    }
}

#endif
