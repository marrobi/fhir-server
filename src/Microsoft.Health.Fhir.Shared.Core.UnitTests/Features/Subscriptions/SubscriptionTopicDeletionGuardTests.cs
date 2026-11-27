// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionTopicDeletionGuardTests : IClassFixture<SubscriptionTestFixture>
    {
        private const string TopicUrl = "http://example.org/topic/encounter";
        private const string Endpoint = "https://hooks.example.com/notify";

        private readonly SubscriptionModelConverter _converter = new();
        private readonly InMemorySubscriptionResources _resources;
        private readonly IAuthorizationService<DataActions> _authorizationService = Substitute.For<IAuthorizationService<DataActions>>();
        private readonly SubscriptionTopicDeletionGuard _guard;

        public SubscriptionTopicDeletionGuardTests(SubscriptionTestFixture fixture)
        {
            var configuration = new SubscriptionsConfiguration { Enabled = true };
            _resources = new InMemorySubscriptionResources(_converter);
            var registry = new SubscriptionRegistry(_resources, _converter, new InMemorySubscriptionStore(), fixture.CreateValidator(configuration), NullLogger<SubscriptionRegistry>.Instance);
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<DataActions>(0));
            _guard = new SubscriptionTopicDeletionGuard(Options.Create(configuration), _converter, _resources, registry, _authorizationService);
        }

#if !Stu3
        [Theory]
        [InlineData("requested", true)]
        [InlineData("active", true)]
        [InlineData("error", true)]
        [InlineData("off", false)]
        public async Task GivenATopicUsedByASubscription_WhenDeleted_ThenOnlyALiveSubscriptionBlocksIt(string status, bool blocked)
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", status: status)));

            // Act
            Task delete = _guard.EnsureCanDeleteAsync([(_converter.TopicResourceType, "t1")], checkPermission: true, removesCurrentVersion: true, CancellationToken.None);

            // Assert
            if (blocked)
            {
                await Assert.ThrowsAsync<ResourceConflictException>(() => delete);
            }
            else
            {
                await delete;
            }
        }

        [Fact]
        public async Task GivenATopicInUse_WhenOnlyItsHistoryIsPurged_ThenItIsAllowed()
        {
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")));

            await _guard.EnsureCanDeleteAsync([(_converter.TopicResourceType, "t1")], checkPermission: true, removesCurrentVersion: false, CancellationToken.None);

            await _authorizationService.Received().CheckAccess(DataActions.SubscriptionTopic, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GivenACallerWithoutTheTopicPermission_WhenATopicIsDeleted_ThenItIsRefusedUnlessABackgroundJobDeletesIt()
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Delete);

            // Act and assert
            await Assert.ThrowsAsync<UnauthorizedFhirActionException>(() =>
                _guard.EnsureCanDeleteAsync([(_converter.TopicResourceType, "t1")], checkPermission: true, removesCurrentVersion: true, CancellationToken.None));
            await _guard.EnsureCanDeleteAsync([(_converter.TopicResourceType, "t1")], checkPermission: false, removesCurrentVersion: true, CancellationToken.None);
        }

#if R4 && !R4B
        [Fact]
        public async Task GivenAnOrdinaryBasic_WhenDeleted_ThenTheTopicPermissionIsNotNeeded()
        {
            // SMART tokens never carry the topic permission, so it must not be required for Basic resources that are not topics.
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _resources.Put(new Basic { Id = "b9", Code = new CodeableConcept("http://example.org", "x") }.ToResourceElement());
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Delete);

            await _guard.EnsureCanDeleteAsync([(KnownResourceTypes.Basic, "b9")], checkPermission: true, removesCurrentVersion: true, CancellationToken.None);

            await _authorizationService.DidNotReceive().CheckAccess(DataActions.SubscriptionTopic, Arg.Any<CancellationToken>());
        }
#endif
#endif

        private static ResourceElement Parse(string json) => new FhirJsonParser().Parse<Resource>(json).ToResourceElement();
    }
}
