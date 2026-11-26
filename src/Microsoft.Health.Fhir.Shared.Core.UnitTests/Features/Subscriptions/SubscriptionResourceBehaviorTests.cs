// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Messages.Create;
using Microsoft.Health.Fhir.Core.Messages.Delete;
using Microsoft.Health.Fhir.Core.Messages.Upsert;
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
    public class SubscriptionResourceBehaviorTests : IClassFixture<SubscriptionTestFixture>
    {
        private const string TopicUrl = "http://example.org/topic/encounter";
        private const string Endpoint = "https://hooks.example.com/notify";

        private readonly SubscriptionModelConverter _converter = new();
        private readonly InMemorySubscriptionStore _store = new();
        private readonly InMemorySubscriptionResources _resources;
        private readonly IAuthorizationService<DataActions> _authorizationService = Substitute.For<IAuthorizationService<DataActions>>();
        private readonly SubscriptionsConfiguration _configuration = new() { Enabled = true, MaxLifetimeDays = 30, MaxActiveSubscriptions = 2 };
        private readonly SubscriptionResourceBehavior _behavior;
        private readonly FhirRequestContext _requestContext;

        public SubscriptionResourceBehaviorTests(SubscriptionTestFixture fixture)
        {
            _resources = new InMemorySubscriptionResources(_converter);
            var registry = new SubscriptionRegistry(_resources, _converter, _store, fixture.CreateValidator(_configuration), NullLogger<SubscriptionRegistry>.Instance);
            var accessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            _requestContext = new FhirRequestContext("POST", "https://fhir.example/Subscription", "https://fhir.example/", "c1", new Dictionary<string, StringValues>(), new Dictionary<string, StringValues>())
            {
                Principal = new System.Security.Claims.ClaimsPrincipal(),
            };
            accessor.RequestContext.Returns(_requestContext);

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(c => c.ArgAt<DataActions>(0));

            _behavior = new SubscriptionResourceBehavior(
                Options.Create(_configuration),
                _converter,
                fixture.CreateValidator(_configuration),
                _resources,
                _store,
                registry,
                fixture.CreateAuthorizer(),
                _authorizationService,
                accessor);
        }

#if !Stu3
        [Fact]
        public async Task GivenATopic_WhenCreatedWithoutTheSubscriptionTopicPermission_ThenItIsRejected()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Read | DataActions.Write);

            await Assert.ThrowsAsync<UnauthorizedFhirActionException>(() => CreateAsync(Parse(SubscriptionResources.Topic(TopicUrl))));
        }

        [Fact]
        public async Task GivenATopic_WhenCreatedWithThePermission_ThenItIsValidatedAndPassedOn()
        {
            UpsertResourceResponse response = await CreateAsync(Parse(SubscriptionResources.Topic(TopicUrl)));

            Assert.NotNull(response);
            await _authorizationService.Received().CheckAccess(DataActions.SubscriptionTopic, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GivenAnInvalidTopic_WhenCreated_ThenItIsRejected()
        {
            string json = SubscriptionResources.Topic(TopicUrl, "t1", [new TopicTrigger("Encounter", ["update"]) { Current = "subject.name=x" }], [new TopicFilter("Encounter", "subject")]);

            await Assert.ThrowsAsync<BadRequestException>(() => CreateAsync(Parse(json)));
        }

        [Fact]
        public async Task GivenAValidSubscription_WhenCreated_ThenStatusIsRequestedIdOnlyIsDefaultedAndAGrantIsSaved()
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            ResourceElement subscription = Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, content: null, status: "active"));

            // Act
            ResourceElement written = null;
            await CreateAsync(subscription, r => written = r);

            // Assert
            SubscriptionInfo info = _converter.ToSubscription(written);
            Assert.Equal(SubscriptionStatusCode.Requested, info.Status);
            Assert.Equal(SubscriptionContentLevel.IdOnly, info.Content);
            SubscriptionGrant grant = SubscriptionGrant.Deserialize(_store.States["new-id"].Grant);
            Assert.Equal(info.ComputeContentHash(), grant.ContentHash);
            Assert.Equal("https://fhir.example/", grant.RequestBase);
        }

        [Theory]
        [InlineData(null, 30)] // no end: the maximum lifetime
        [InlineData(365, 30)] // beyond the maximum lifetime: capped
        [InlineData(7, 7)] // within the maximum lifetime: kept
        public async Task GivenASubscriptionEnd_WhenCreated_ThenItIsLimitedToTheMaximumLifetime(int? requestedDays, int expectedDays)
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ResourceElement subscription = Parse(SubscriptionResources.Subscription(
                TopicUrl,
                Endpoint,
                end: requestedDays.HasValue ? now.AddDays(requestedDays.Value) : null,
                withoutEnd: !requestedDays.HasValue));

            // Act
            ResourceElement written = null;
            await CreateAsync(subscription, r => written = r);

            // Assert
            DateTimeOffset? end = _converter.ToSubscription(written).End;
            Assert.NotNull(end);
            Assert.InRange(end.Value, now.AddDays(expectedDays).AddMinutes(-1), now.AddDays(expectedDays).AddMinutes(1));
        }

        [Fact]
        public async Task GivenASubscriptionForAnUnknownTopic_WhenCreated_ThenItIsRejected()
        {
            await Assert.ThrowsAsync<BadRequestException>(() => CreateAsync(Parse(SubscriptionResources.Subscription("http://example.org/none", Endpoint))));
        }

        [Fact]
        public async Task GivenASubscriptionToAPrivateEndpoint_WhenCreated_ThenItIsRejected()
        {
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));

            await Assert.ThrowsAsync<BadRequestException>(() => CreateAsync(Parse(SubscriptionResources.Subscription(TopicUrl, "https://169.254.169.254/latest"))));
        }

        [Fact]
        public async Task GivenAnActiveSubscription_WhenUpdatedWithoutContentChanges_ThenItStaysActive()
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", status: "active")));

            // Act
            ResourceElement unchanged = null;
            ResourceElement moved = null;
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", status: "active")), r => unchanged = r);
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, "https://other.example.com/", id: "s1", status: "active")), r => moved = r);

            // Assert
            Assert.Equal(SubscriptionStatusCode.Active, _converter.ToSubscription(unchanged).Status);
            Assert.Equal(SubscriptionStatusCode.Requested, _converter.ToSubscription(moved).Status);
        }

        [Fact]
        public async Task GivenTheClientSetsOff_WhenUpdated_ThenOffIsKept()
        {
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            ResourceElement written = null;

            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", status: "off")), r => written = r);

            Assert.Equal(SubscriptionStatusCode.Off, _converter.ToSubscription(written).Status);
        }

        [Fact]
        public async Task GivenTheActiveSubscriptionLimit_WhenAnotherIsCreated_ThenItIsRejected()
        {
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "a")));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "b")));

            await Assert.ThrowsAsync<BadRequestException>(() => CreateAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint))));
        }

        [Fact]
        public async Task GivenTheActiveSubscriptionLimit_WhenAnOffSubscriptionIsRequestedAgain_ThenItIsRejected()
        {
            // Arrange: at the limit, with an off subscription that does not count towards it.
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "a")));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "b")));
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "c", status: "off")));

            // Act and assert
            await Assert.ThrowsAsync<BadRequestException>(() => UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "c"))));
        }

        [Fact]
        public async Task GivenASubscriptionWithAGrant_WhenTheClientSetsOff_ThenTheGrantIsRevokedSoItCannotBeReactivatedWithoutTheApi()
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")));
            Assert.NotNull(_store.States["s1"].Grant);

            // Act
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", status: "off")));

            // Assert
            Assert.Null(_store.States["s1"].Grant);
            Assert.Null(_store.States["s1"].ContentHash);
        }

        [Fact]
        public async Task GivenASubscriptionGrantedToOneIdentity_WhenAnotherIdentityWritesItWithoutContentChanges_ThenTheStoredGrantIsKept()
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            SignInAs("creator");
            ResourceElement created = null;
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")), r => created = r);
            _resources.Put(created);

            // Act
            SignInAs("administrator");
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")));

            // Assert
            Assert.Equal("creator", SubscriptionGrant.Deserialize(_store.States["s1"].Grant).Subject);
        }

        [Fact]
        public async Task GivenASubscriptionGrantedToOneIdentity_WhenAnotherIdentityChangesItsContent_ThenTheWritersGrantIsCaptured()
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            SignInAs("creator");
            ResourceElement created = null;
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")), r => created = r);
            _resources.Put(created);

            // Act
            SignInAs("administrator");
            ResourceElement moved = null;
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, "https://other.example.com/", id: "s1")), r => moved = r);

            // Assert
            Assert.Equal("administrator", SubscriptionGrant.Deserialize(_store.States["s1"].Grant).Subject);
            Assert.Equal(SubscriptionStatusCode.Requested, _converter.ToSubscription(moved).Status);
        }

        [Fact]
        public async Task GivenAKeptGrant_WhenAnotherIdentityExtendsTheEnd_ThenTheLifetimeStillCountsFromTheGrant()
        {
            // Arrange: a grant captured 20 days ago, with a 30-day maximum lifetime.
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            ResourceElement existing = Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1"));
            _resources.Put(existing);
            string hash = _converter.ToSubscription(existing).ComputeContentHash();
            DateTimeOffset grantedUtc = DateTimeOffset.UtcNow.AddDays(-20);
            await _store.SaveGrantAsync("s1", hash, new SubscriptionGrant { ContentHash = hash, Subject = "creator", CreatedUtc = grantedUtc }.Serialize(), CancellationToken.None);
            SignInAs("administrator");

            // Act
            ResourceElement written = null;
            await UpsertAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", end: DateTimeOffset.UtcNow.AddDays(30))), r => written = r);

            // Assert
            DateTimeOffset? end = _converter.ToSubscription(written).End;
            Assert.NotNull(end);
            Assert.InRange(end.Value, grantedUtc.AddDays(30).AddMinutes(-1), grantedUtc.AddDays(30).AddMinutes(1));
            Assert.Equal("creator", SubscriptionGrant.Deserialize(_store.States["s1"].Grant).Subject);
        }

        [Theory]
        [InlineData(SubscriptionStoreAvailability.NotSupported, typeof(OperationNotImplementedException))]
        [InlineData(SubscriptionStoreAvailability.NotReady, typeof(ServiceUnavailableException))]
        public async Task GivenTheStoreIsUnavailable_WhenASubscriptionIsWritten_ThenItIsRejectedWithoutWriting(SubscriptionStoreAvailability availability, Type expected)
        {
            // Arrange
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _store.Availability = availability;
            bool written = false;

            // Act
            Exception ex = await Record.ExceptionAsync(() => CreateAsync(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint)), _ => written = true));

            // Assert
            Assert.IsType(expected, ex);
            Assert.False(written);
        }

        [Fact]
        public async Task GivenASubscription_WhenDeleted_ThenItsStateIsRemoved()
        {
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);

            await _behavior.HandleAsync(new DeleteResourceRequest("Subscription", "s1", DeleteOperation.SoftDelete), () => Task.FromResult(new DeleteResourceResponse(new ResourceKey("Subscription", "s1"))), CancellationToken.None);

            Assert.False(_store.States.ContainsKey("s1"));
        }

        [Fact]
        public async Task GivenASubscription_WhenItsHistoryIsPurged_ThenItsStateIsKept()
        {
            await _store.SaveGrantAsync("s1", "h", "{}", CancellationToken.None);

            await _behavior.HandleAsync(new DeleteResourceRequest("Subscription", "s1", DeleteOperation.PurgeHistory), () => Task.FromResult(new DeleteResourceResponse(new ResourceKey("Subscription", "s1"))), CancellationToken.None);

            Assert.Equal("{}", _store.States["s1"].Grant);
        }

        [Fact]
        public async Task GivenAStoredTopic_WhenDeletedWithoutThePermission_ThenItIsRejected()
        {
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Delete);

            await Assert.ThrowsAsync<UnauthorizedFhirActionException>(() => _behavior.HandleAsync(
                new DeleteResourceRequest(_converter.TopicResourceType, "t1", DeleteOperation.SoftDelete),
                () => Task.FromResult(new DeleteResourceResponse(new ResourceKey(_converter.TopicResourceType, "t1"))),
                CancellationToken.None));
        }

#if R4 && !R4B
        [Fact]
        public async Task GivenAStoredBasicTopic_WhenOverwrittenWithAnOrdinaryBasic_ThenThePermissionIsStillRequired()
        {
            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Write);
            var ordinary = new Basic { Id = "t1", Code = new CodeableConcept("http://example.org", "x") };

            await Assert.ThrowsAsync<UnauthorizedFhirActionException>(() => UpsertAsync(ordinary.ToResourceElement()));
        }

        [Fact]
        public async Task GivenAnOrdinaryBasic_WhenWritten_ThenThePermissionIsNotRequired()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Write);
            var ordinary = new Basic { Id = "b9", Code = new CodeableConcept("http://example.org", "x") };

            Assert.NotNull(await UpsertAsync(ordinary.ToResourceElement()));
        }
#endif
#endif

        [Fact]
        public async Task GivenTheFeatureIsDisabled_WhenAnythingIsWritten_ThenItPassesThroughUnchecked()
        {
            _configuration.Enabled = false;

            Assert.NotNull(await CreateAsync(Parse(SubscriptionResources.Subscription("http://example.org/none", "http://10.0.0.1/"))));
        }

#if Stu3
        [Fact]
        public async Task GivenStu3WithTheFeatureEnabled_WhenASubscriptionIsWritten_ThenItPassesThroughUnchangedWithoutAGrant()
        {
            // Arrange
            ResourceElement subscription = Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, status: "active"));

            // Act
            ResourceElement written = null;
            await CreateAsync(subscription, r => written = r);

            // Assert
            Assert.Equal("active", written.Scalar<string>("Resource.status"));
            Assert.Empty(_store.States);
        }
#endif

        private void SignInAs(string subject)
        {
            _requestContext.Principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("oid", subject)], "test"));
        }

        private System.Threading.Tasks.Task<UpsertResourceResponse> CreateAsync(ResourceElement resource, Action<ResourceElement> capture = null)
        {
            return _behavior.HandleAsync(new CreateResourceRequest(resource), () => Respond(resource, capture), CancellationToken.None);
        }

        private System.Threading.Tasks.Task<UpsertResourceResponse> UpsertAsync(ResourceElement resource, Action<ResourceElement> capture = null)
        {
            return _behavior.HandleAsync(new UpsertResourceRequest(resource), () => Respond(resource, capture), CancellationToken.None);
        }

        private static System.Threading.Tasks.Task<UpsertResourceResponse> Respond(ResourceElement resource, Action<ResourceElement> capture)
        {
            Resource poco = resource.ToPoco<Resource>();
            poco.Id ??= "new-id";
            ResourceElement element = poco.ToResourceElement();
            capture?.Invoke(element);
            var wrapper = new ResourceWrapper(element, new RawResource(poco.ToJson(), FhirResourceFormat.Json, true), new ResourceRequest(HttpMethod.Post, "http://fhir"), false, null, null, null, null);
            return Task.FromResult(new UpsertResourceResponse(new SaveOutcome(new RawResourceElement(wrapper), SaveOutcomeType.Created)));
        }

        private static ResourceElement Parse(string json) => new FhirJsonParser().Parse<Resource>(json).ToResourceElement();
    }
}
