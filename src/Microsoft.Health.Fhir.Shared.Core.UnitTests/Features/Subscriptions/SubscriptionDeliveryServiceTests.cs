// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Delivery;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
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
    public class SubscriptionDeliveryServiceTests : IClassFixture<SubscriptionTestFixture>
    {
        private const string TopicUrl = "http://example.org/topic/encounter";
        private const string Endpoint = "https://hooks.example.com/notify";

        private readonly SubscriptionModelConverter _converter = new();
        private readonly InMemorySubscriptionStore _store = new();
        private readonly InMemorySubscriptionResources _resources;
        private readonly RecordingChannel _channel = new();
        private readonly SubscriptionsConfiguration _configuration = new() { Enabled = true, MaxDeliveryAttempts = 3, RetryBaseDelaySeconds = 0, RegistryRefreshIntervalSeconds = 0 };
        private readonly SubscriptionAuthorizer _authorizer;
        private readonly SubscriptionDeliveryService _service;
        private readonly ISubscriptionMetricHandler _metrics = Substitute.For<ISubscriptionMetricHandler>();

        public SubscriptionDeliveryServiceTests(SubscriptionTestFixture fixture)
        {
            _resources = new InMemorySubscriptionResources(_converter);
            _authorizer = fixture.CreateAuthorizer();
            var registry = new SubscriptionRegistry(_resources, _converter, _store, fixture.CreateValidator(_configuration), NullLogger<SubscriptionRegistry>.Instance);
            _service = new SubscriptionDeliveryService(
                Options.Create(_configuration),
                registry,
                _store,
                _resources,
                _converter,
                new SubscriptionNotificationFactory(),
                _resources,
                _authorizer,
                [_channel],
                new FhirRequestContextAccessor(),
                NullLogger<SubscriptionDeliveryService>.Instance,
                _metrics);

            _resources.Put(Parse(SubscriptionResources.Topic(TopicUrl)));
        }

        [Fact]
        public async Task GivenARequestedSubscriptionWithAGrant_WhenTheHandshakeSucceeds_ThenItBecomesActiveAndIsActivated()
        {
            // Arrange
            await AddSubscriptionAsync("s1");

            // Act
            await RunAsync();

            // Assert
            Assert.Contains("handshake", Assert.Single(_channel.Payloads), StringComparison.Ordinal);
            Assert.Equal(SubscriptionStatusCode.Active, Status("s1"));
            Assert.Equal(_store.Visibility, _store.States["s1"].ActivationTransactionId);
            _metrics.Received(1).NotificationSent(nameof(SubscriptionNotificationType.Handshake), true, Arg.Any<TimeSpan>());
            _metrics.Received(1).StatusChanged(nameof(SubscriptionStatusCode.Active));
        }

        [Fact]
        public async Task GivenARequestedSubscription_WhenTheHandshakeFails_ThenItIsSetToErrorAndNotActivated()
        {
            await AddSubscriptionAsync("s1");
            _channel.Results.Enqueue(ChannelDeliveryResult.Failure("HTTP 500"));

            await RunAsync();

            Assert.Equal(SubscriptionStatusCode.Error, Status("s1"));
            Assert.Null(_store.States["s1"].ActivationTransactionId);
            Assert.Contains("HTTP 500", _store.States["s1"].Error, StringComparison.Ordinal);
            _metrics.Received(1).NotificationSent(nameof(SubscriptionNotificationType.Handshake), false, Arg.Any<TimeSpan>());
            _metrics.Received(1).StatusChanged(nameof(SubscriptionStatusCode.Error));
        }

        [Fact]
        public async Task GivenASubscriptionWithoutAGrant_WhenProcessed_ThenItIsSetToOffWithoutAHandshake()
        {
            // Simulates $import or bulk writes, which never capture a grant.
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")), DateTimeOffset.UtcNow.AddMinutes(-2));

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionStatusCode.Off, Status("s1"));
        }

        [Fact]
        public async Task GivenAGrantForDifferentContent_WhenProcessed_ThenTheSubscriptionIsNotActivated()
        {
            await AddSubscriptionAsync("s1");
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, "https://attacker.example.com/", id: "s1")), DateTimeOffset.UtcNow.AddMinutes(-2));

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionStatusCode.Off, Status("s1"));
        }

        [Fact]
        public async Task GivenAnActiveSubscriptionThatWasNeverActivated_WhenProcessed_ThenItReturnsToRequestedAndThenHandshakes()
        {
            await AddSubscriptionAsync("s1", status: "active");

            await RunAsync();
            Assert.Equal(SubscriptionStatusCode.Requested, Status("s1"));

            await RunAsync();
            Assert.Equal(SubscriptionStatusCode.Active, Status("s1"));
        }

        [Fact]
        public async Task GivenPendingEvents_WhenDelivered_ThenTheyAreSentInOrderInBatchesAndMarkedDelivered()
        {
            // Arrange
            await ActivateAsync("s1");
            for (int i = 1; i <= 12; i++)
            {
                _store.AddEvent("s1", "Encounter", "e" + i, 1);
            }

            // Act
            await RunAsync();

            // Assert: maxCount is 10, so two notifications in event-number order.
            Assert.Equal(2, _channel.Payloads.Count);
            Assert.All(_store.Events, e => Assert.Equal(SubscriptionEventStatus.Delivered, e.Status));
            Bundle first = new FhirJsonParser().Parse<Bundle>(_channel.Payloads[0]);
            Bundle second = new FhirJsonParser().Parse<Bundle>(_channel.Payloads[1]);
            Assert.Equal(11, first.Entry.Count);
            Assert.Equal(3, second.Entry.Count);
            Assert.Contains("e11", second.Entry[1].FullUrl, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GivenADeliveryFailure_WhenRetried_ThenTheSameEventNumbersAreResentUntilTheLimitSetsError()
        {
            // Arrange
            await ActivateAsync("s1");
            _store.AddEvent("s1", "Encounter", "e1", 1);
            for (int i = 0; i < 3; i++)
            {
                _channel.Results.Enqueue(ChannelDeliveryResult.Failure("HTTP 503"));
            }

            // Act
            await RunAsync();
            await RunAsync();
            await RunAsync();

            // Assert
            Assert.Equal(3, _channel.Payloads.Count);
            Assert.Equal(SubscriptionStatusCode.Error, Status("s1"));
            SubscriptionEventRecord retained = Assert.Single(_store.Events);
            Assert.Equal(SubscriptionEventStatus.Pending, retained.Status);
            Assert.Equal(1, retained.EventNumber);
            Assert.Equal(3, retained.Attempts);

            // Act: the subscriber fixes the endpoint and resets the status to requested.
            await UpdateSubscriptionAsync("s1", "requested");
            await RunAsync();
            await RunAsync();

            // Assert: handshake, then the retained event is delivered with its original number.
            Assert.Equal(SubscriptionStatusCode.Active, Status("s1"));
            Assert.Equal(SubscriptionEventStatus.Delivered, Assert.Single(_store.Events).Status);
        }

        [Theory]
        [InlineData(1, SubscriptionStatusCode.Error, SubscriptionEventStatus.Pending)]
        [InlineData(100, SubscriptionStatusCode.Off, SubscriptionEventStatus.NotAuthorized)]
        public async Task GivenASubscriptionInError_WhenProcessedAfterSomeHours_ThenItIsTurnedOffOnlyAfterTheLimit(int hoursInError, SubscriptionStatusCode expectedStatus, SubscriptionEventStatus expectedEventStatus)
        {
            // Arrange
            _configuration.MaxErrorHours = 72;
            await ActivateAsync("s1");
            _store.AddEvent("s1", "Encounter", "e1", 1);
            for (int i = 0; i < 3; i++)
            {
                _channel.Results.Enqueue(ChannelDeliveryResult.Failure("HTTP 503"));
                await RunAsync();
            }

            Assert.Equal(SubscriptionStatusCode.Error, Status("s1"));
            _resources.Put(_resources.Resources[("Subscription", "s1")], DateTimeOffset.UtcNow.AddHours(-hoursInError));

            // Act
            await RunAsync();

            // Assert
            Assert.Equal(expectedStatus, Status("s1"));
            Assert.Equal(expectedEventStatus, Assert.Single(_store.Events).Status);
        }

        [Fact]
        public async Task GivenAGrantThatNoLongerAllowsTheResourceType_WhenDelivering_ThenTheEventIsNotSent()
        {
            await ActivateAsync("s1", new SubscriptionGrant { Smart = true, Scopes = [new SubscriptionGrantScope { Resource = "Patient", DataActions = Microsoft.Health.Fhir.Core.Features.Security.DataActions.Read }] });
            _channel.Payloads.Clear();
            _store.AddEvent("s1", "Encounter", "e1", 1);

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionEventStatus.NotAuthorized, Assert.Single(_store.Events).Status);
        }

        [Fact]
        public async Task GivenADeletedSubscription_WhenProcessed_ThenItsStateAndEventsAreRemoved()
        {
            await ActivateAsync("s1");
            _store.AddEvent("s1", "Encounter", "e1", 1);
            _resources.Resources.TryRemove(("Subscription", "s1"), out _);

            await RunAsync();

            Assert.False(_store.States.ContainsKey("s1"));
            Assert.Empty(_store.Events);
        }

        [Fact]
        public async Task GivenAnEndedSubscription_WhenProcessed_ThenItIsSetToOffAndDeactivated()
        {
            await ActivateAsync("s1");
            ResourceElement current = _resources.Resources[("Subscription", "s1")];
            _converter.ApplyServerValues(current, SubscriptionStatusCode.Active, DateTimeOffset.UtcNow.AddMinutes(-1));
            _resources.Put(current.ToPoco<Resource>().ToResourceElement());

            await RunAsync();

            Assert.Equal(SubscriptionStatusCode.Off, Status("s1"));
            Assert.Null(_store.States["s1"].ActivationTransactionId);
            Assert.Null(_store.States["s1"].Grant);
        }

        [Fact]
        public async Task GivenAJustWrittenSubscriptionWhoseGrantIsNotSavedYet_WhenProcessed_ThenItIsLeftRequested()
        {
            // An API write commits the resource just before its grant; the subscription must not be turned off in between.
            _resources.Put(Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1")));

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionStatusCode.Requested, Status("s1"));
        }

        [Fact]
        public async Task GivenASubscriptionWrittenWithoutTheApiWithAnEndBeyondTheMaximumLifetime_WhenProcessed_ThenItIsSetToError()
        {
            // Arrange: an existing grant, but the stored end was moved beyond the limit outside the API.
            ResourceElement resource = Parse(SubscriptionResources.Subscription(TopicUrl, Endpoint, id: "s1", heartbeat: 3600, end: DateTimeOffset.UtcNow.AddDays(_configuration.MaxLifetimeDays + 30)));
            _resources.Put(resource);
            SubscriptionInfo info = _converter.ToSubscription(_resources.Resources[("Subscription", "s1")]);
            await _store.SaveGrantAsync("s1", info.ComputeContentHash(), new SubscriptionGrant { ContentHash = info.ComputeContentHash() }.Serialize(), CancellationToken.None);

            // Act
            await RunAsync();

            // Assert
            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionStatusCode.Error, Status("s1"));
        }

        [Fact]
        public async Task GivenPendingEvents_WhenEachNotificationIsSent_ThenTheLeaseIsRenewedForLongerThanTheTimeout()
        {
            // Arrange: a lease shorter than the subscription's 30 second timeout.
            _configuration.DeliveryLeaseSeconds = 10;
            await ActivateAsync("s1");
            _store.LeaseRequests.Clear();
            for (int i = 1; i <= 3; i++)
            {
                _store.AddEvent("s1", "Encounter", "e" + i, 1);
            }

            // Act
            await RunAsync();

            // Assert: the first request is the lease for the pass; every send renews it for timeout plus margin.
            Assert.NotEmpty(_channel.Payloads);
            List<TimeSpan> renewals = _store.LeaseRequests.Skip(1).Select(r => r.Period).ToList();
            Assert.Equal(_channel.Payloads.Count, renewals.Count);
            Assert.All(renewals, p => Assert.True(p >= TimeSpan.FromSeconds(60), $"Lease renewed for {p}."));
        }

        [Fact]
        public async Task GivenTheLeaseIsTakenByAnotherWorker_WhenANotificationIsAboutToBeSent_ThenItIsNotSent()
        {
            // Arrange
            await ActivateAsync("s1");
            _store.AddEvent("s1", "Encounter", "e1", 1);
            _store.LeaseTakenOnRenewal = "other-worker";

            // Act
            await RunAsync();

            // Assert
            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionEventStatus.Pending, Assert.Single(_store.Events).Status);
        }

        [Fact]
        public async Task GivenTheStoreIsNotReady_WhenProcessed_ThenNothingIsDoneUntilItIs()
        {
            await AddSubscriptionAsync("s1");
            _store.Availability = SubscriptionStoreAvailability.NotReady;

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionStatusCode.Requested, Status("s1"));
        }

        [Fact]
        public async Task GivenAnActiveSubscription_WhenTheHeartbeatIsDue_ThenAHeartbeatIsSent()
        {
            await ActivateAsync("s1");
            _channel.Payloads.Clear();
            await _store.SetLastNotificationAsync("s1", DateTimeOffset.UtcNow.AddHours(-2), CancellationToken.None);

            await RunAsync();

            Assert.Contains("heartbeat", Assert.Single(_channel.Payloads), StringComparison.Ordinal);
        }

        [Fact]
        public async Task GivenASubscriptionWhoseTopicIsMissing_WhenProcessed_ThenItIsSetToError()
        {
            await AddSubscriptionAsync("s1", topic: "http://example.org/topic/missing");

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Equal(SubscriptionStatusCode.Error, Status("s1"));
        }

        [Fact]
        public async Task GivenAnOffSubscription_WhenProcessed_ThenItIsDeactivatedAndNothingIsSent()
        {
            await ActivateAsync("s1");
            _channel.Payloads.Clear();
            await UpdateSubscriptionAsync("s1", "off");

            await RunAsync();

            Assert.Empty(_channel.Payloads);
            Assert.Null(_store.States["s1"].ActivationTransactionId);
        }

        [Fact]
        public async Task GivenTheFeatureIsDisabled_WhenTheServiceStarts_ThenItDoesNothing()
        {
            _configuration.Enabled = false;
            await AddSubscriptionAsync("s1");

            await _service.StartAsync(CancellationToken.None);
            await _service.StopAsync(CancellationToken.None);

            Assert.Empty(_channel.Payloads);
        }

        private async Task RunAsync() => await _service.RunOnceAsync(CancellationToken.None);

        private async Task AddSubscriptionAsync(string id, string status = "requested", string topic = TopicUrl, SubscriptionGrant grant = null)
        {
            ResourceElement resource = Parse(SubscriptionResources.Subscription(topic, Endpoint, id: id, status: status, heartbeat: 3600));
            _resources.Put(resource);
            SubscriptionInfo info = _converter.ToSubscription(_resources.Resources[("Subscription", id)]);
            grant ??= new SubscriptionGrant();
            var bound = new SubscriptionGrant { ContentHash = info.ComputeContentHash(), Smart = grant.Smart, Scopes = grant.Scopes, RequestBase = "https://fhir.example/" };
            await _store.SaveGrantAsync(id, bound.ContentHash, bound.Serialize(), CancellationToken.None);
        }

        private async Task ActivateAsync(string id, SubscriptionGrant grant = null)
        {
            await AddSubscriptionAsync(id, grant: grant);
            await RunAsync();
            Assert.Equal(SubscriptionStatusCode.Active, Status(id));
            _channel.Payloads.Clear();
        }

        private async Task UpdateSubscriptionAsync(string id, string status)
        {
            ResourceElement current = _resources.Resources[("Subscription", id)];
            _resources.Put(_converter.WithStatus(current, status == "off" ? SubscriptionStatusCode.Off : SubscriptionStatusCode.Requested, null));
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private SubscriptionStatusCode Status(string id) => _converter.ToSubscription(_resources.Resources[("Subscription", id)]).Status;

        private static ResourceElement Parse(string json) => new FhirJsonParser().Parse<Resource>(json).ToResourceElement();
    }
}
#endif
