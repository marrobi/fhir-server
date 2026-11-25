// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Conformance.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionsCapabilityProviderTests : IClassFixture<SubscriptionTestFixture>
    {
        private const string TopicUrl = "http://example.org/topic/encounter";

        private readonly SubscriptionModelConverter _converter = new();
        private readonly InMemorySubscriptionStore _store = new();
        private readonly InMemorySubscriptionResources _resources;
        private readonly SubscriptionsCapabilityProvider _provider;
        private readonly ConcurrentDictionary<string, ListedResourceComponent> _components = new(StringComparer.Ordinal);
        private readonly ICapabilityStatementBuilder _builder = Substitute.For<ICapabilityStatementBuilder>();

        public SubscriptionsCapabilityProviderTests(SubscriptionTestFixture fixture)
        {
            var configuration = new SubscriptionsConfiguration { Enabled = true, RegistryRefreshIntervalSeconds = 0 };
            _resources = new InMemorySubscriptionResources(_converter);
            var registry = new SubscriptionRegistry(_resources, _converter, _store, fixture.CreateValidator(configuration), NullLogger<SubscriptionRegistry>.Instance);
            _provider = new SubscriptionsCapabilityProvider(Options.Create(configuration), _converter, ModelInfoProvider.Instance, registry, _store, NullLogger<SubscriptionsCapabilityProvider>.Instance);

            _builder.ApplyToResource(Arg.Any<string>(), Arg.Any<Action<ListedResourceComponent>>()).Returns(call =>
            {
                call.Arg<Action<ListedResourceComponent>>()(_components.GetOrAdd(call.Arg<string>(), _ => new ListedResourceComponent()));
                return _builder;
            });
        }

        [Fact]
        public async Task GivenAStoreThatIsNotReadyYet_WhenTheStatementIsBuilt_ThenSubscriptionSupportIsStillDeclared()
        {
            // Arrange: for example while the database schema is being upgraded; the statement is built only once.
            _store.Availability = SubscriptionStoreAvailability.NotReady;

            // Act
            await _provider.BuildAsync(_builder, CancellationToken.None);

            // Assert
            Assert.Contains(_components[KnownResourceTypes.Subscription].Operation, o => o.Name == "status");
#if !R5
            Assert.Contains(SubscriptionsBackport.SubscriptionProfile, _components[KnownResourceTypes.Subscription].SupportedProfile);
            Assert.NotEmpty(await _provider.GetCanonicalUrlsAsync(CancellationToken.None));
#endif
        }

        [Fact]
        public async Task GivenAStoreThatDoesNotSupportSubscriptions_WhenTheStatementIsBuilt_ThenNothingIsDeclared()
        {
            _store.Availability = SubscriptionStoreAvailability.NotSupported;

            await _provider.BuildAsync(_builder, CancellationToken.None);

            Assert.False(_components.ContainsKey(KnownResourceTypes.Subscription));
            Assert.Empty(await _provider.GetCanonicalUrlsAsync(CancellationToken.None));
        }

#if R4 && !R4B
        [Fact]
        public async Task GivenTopicsChange_WhenTheStatementIsRefreshed_ThenTheTopicCanonicalsFollow()
        {
            // Arrange
            await _provider.BuildAsync(_builder, CancellationToken.None);
            Assert.False(_components[KnownResourceTypes.Subscription].AdditionalData.ContainsKey("extension"));

            // Act: a topic is created.
            _resources.Put(new FhirJsonParser().Parse<Hl7.Fhir.Model.Resource>(SubscriptionResources.Topic(TopicUrl)).ToResourceElement());
            await _provider.RefreshAsync(_builder, CancellationToken.None);

            // Assert
            JToken extension = Assert.Single((JArray)_components[KnownResourceTypes.Subscription].AdditionalData["extension"]);
            Assert.Equal(SubscriptionsBackport.TopicCanonicalCapability, (string)extension["url"]);
            Assert.Equal(TopicUrl, (string)extension["valueCanonical"]);

            // Act: the topic is deleted.
            _resources.Resources.Clear();
            await _provider.RefreshAsync(_builder, CancellationToken.None);

            // Assert
            Assert.False(_components[KnownResourceTypes.Subscription].AdditionalData.ContainsKey("extension"));
        }
#endif
    }
}

#endif
