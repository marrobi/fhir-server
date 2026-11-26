// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.BulkUpdate;
using Microsoft.Health.Fhir.Core.Features.Operations.BulkUpdate.Handlers;
using Microsoft.Health.Fhir.Core.Features.Operations.BulkUpdate.Messages;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.JobManagement;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using JobConflictException = Microsoft.Health.JobManagement.JobConflictException;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Operations.BulkUpdate
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.BulkUpdate)]
    public class CreateBulkUpdateHandlerTests
    {
        private readonly IAuthorizationService<DataActions> _authorizationService;
        private readonly IQueueClient _queueClient;
        private readonly RequestContextAccessor<IFhirRequestContext> _contextAccessor;
        private readonly ISearchService _searchService;
        private readonly ISubscriptionModelConverter _subscriptionModelConverter = Substitute.For<ISubscriptionModelConverter>();
        private readonly SubscriptionsConfiguration _subscriptionsConfiguration = new();
        private readonly CreateBulkUpdateHandler _handler;
        private readonly string _testUrl = "https://test.com/";

        public CreateBulkUpdateHandlerTests()
        {
            _authorizationService = Substitute.For<IAuthorizationService<DataActions>>();
            _queueClient = Substitute.For<IQueueClient>();
            _searchService = Substitute.For<ISearchService>();

            _contextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();
            _contextAccessor.RequestContext = new FhirRequestContext(
                method: "BulkUpdateTest",
                uriString: _testUrl,
                baseUriString: _testUrl,
                correlationId: "BulkUpdateTest",
                requestHeaders: new Dictionary<string, StringValues>(),
                responseHeaders: new Dictionary<string, StringValues>());

            _handler = new CreateBulkUpdateHandler(
                _authorizationService,
                _queueClient,
                _contextAccessor,
                _searchService,
                Substitute.For<IResourceSerializer>(),
                Substitute.For<ILogger<CreateBulkUpdateHandler>>(),
                Options.Create(new CoreFeatureConfiguration { EnableSmartBulkUpdateRestriction = true }),
                _subscriptionModelConverter,
                Options.Create(_subscriptionsConfiguration));
        }

        [Theory]
        [MemberData(nameof(GetSearchParamsForJobCreation))]
        public async Task GivenBulkUpdateRequestWithVariousSearchParams_WhenJobCreationRequested_ThenJobIsCreated(List<Tuple<string, string>> searchParams)
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(args =>
            {
                var definition = JsonConvert.DeserializeObject<BulkUpdateDefinition>(args.ArgAt<string[]>(1)[0]);
                Assert.Equal(_testUrl, definition.Url);
                Assert.Equal(_testUrl, definition.BaseUrl);
                Assert.Equal(searchParams?.Count ?? 0, definition.SearchParameters.Count);
                Assert.False(definition.AllowProfileResourceModification);
                return new List<JobInfo>()
                {
                    new JobInfo()
                    {
                        Id = 1,
                    },
                };
            });

            Parameters parameters = GenerateParameters("replace");
            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, parameters, false);

            var response = await _handler.HandleAsync(request, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(1, response.Id);
            await _queueClient.ReceivedWithAnyArgs(1).EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), false, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenResourceTypeIsNull_ThenJobIsCreated()
        {
            var searchParams = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("param", "value"),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(args =>
            {
                var definition = JsonConvert.DeserializeObject<BulkUpdateDefinition>(args.ArgAt<string[]>(1)[0]);
                Assert.Equal(_testUrl, definition.Url);
                Assert.Equal(_testUrl, definition.BaseUrl);
                Assert.Equal(searchParams.Count, definition.SearchParameters.Count);
                Assert.Null(definition.Type);

                return new List<JobInfo>()
                    {
                        new JobInfo()
                        {
                            Id = 1,
                        },
                    };
            });
            Parameters parameters = GenerateParameters("upsert");
            var request = new CreateBulkUpdateRequest(null, searchParams, parameters, false);
            var response = await _handler.HandleAsync(request, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(1, response.Id);
            await _queueClient.ReceivedWithAnyArgs(1).EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), false, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenSearchParamsIsNull_ThenJobIsCreated()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(args =>
            {
                var definition = JsonConvert.DeserializeObject<BulkUpdateDefinition>(args.ArgAt<string[]>(1)[0]);
                Assert.Equal(_testUrl, definition.Url);
                Assert.Equal(_testUrl, definition.BaseUrl);
                Assert.Empty(definition.SearchParameters);

                return new List<JobInfo>()
                    {
                        new JobInfo()
                        {
                            Id = 1,
                        },
                    };
            });

            Parameters parameters = GenerateParameters("replace");
            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, null, parameters, false);

            var response = await _handler.HandleAsync(request, CancellationToken.None);
            Assert.NotNull(response);
            Assert.Equal(1, response.Id);
            await _queueClient.ReceivedWithAnyArgs(1).EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), false, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenQueueClientThrowsUnexpectedException_ThenExceptionIsPropagated()
        {
            var searchParams = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("param", "value"),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>())
                .Throws(new InvalidOperationException("Unexpected error"));

            Parameters parameters = GenerateParameters("replace");
            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, parameters, false);

            await Assert.ThrowsAsync<InvalidOperationException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
        }

        [Fact]
        public async Task GivenSmartFineGrainedContextAndRestrictionEnabled_WhenJobCreationRequested_ThenUnauthorizedFhirActionExceptionIsThrown()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, new List<Tuple<string, string>>(), GenerateParameters("replace"), false);

            await Assert.ThrowsAsync<UnauthorizedFhirActionException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
            await _queueClient.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default, default, default, default);
        }

        [Fact]
        public async Task GivenSmartFineGrainedContextAndRestrictionDisabled_WhenJobCreationRequested_ThenJobIsCreated()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.AccessControlContext.ApplyFineGrainedAccessControl = true;
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(
                new List<JobInfo>
                {
                    new() { Id = 1 },
                });
            var handler = new CreateBulkUpdateHandler(
                _authorizationService,
                _queueClient,
                _contextAccessor,
                _searchService,
                Substitute.For<IResourceSerializer>(),
                Substitute.For<ILogger<CreateBulkUpdateHandler>>(),
                Options.Create(new CoreFeatureConfiguration { EnableSmartBulkUpdateRestriction = false }),
                _subscriptionModelConverter,
                Options.Create(_subscriptionsConfiguration));

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, new List<Tuple<string, string>>(), GenerateParameters("replace"), false);

            var response = await handler.HandleAsync(request, CancellationToken.None);

            Assert.Equal(1, response.Id);
            await _queueClient.Received(1).EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenJobCreationRequestedWhileAnotherJobAlreadyRunning_ThenBadRequestIsReturned()
        {
            var searchParams = new List<Tuple<string, string>>()
            {
                new Tuple<string, string>("param", "value"),
            };
            Parameters parameters = GenerateParameters("replace");
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Throws(new JobManagement.JobConflictException("There are other active job groups"));

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, parameters, false);

            var ex = await Assert.ThrowsAsync<BadRequestException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
            await _queueClient.ReceivedWithAnyArgs(1).EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>());
            Assert.Equal("A bulk update job is already running.", ex.Message);
        }

        [Fact]
        public async Task GivenBulkUpdateRequestWithInvalidSearchParameter_WhenJobCreationRequested_ThenBadRequestIsReturned()
        {
            var searchParams = new List<Tuple<string, string>>()
            {
                new Tuple<string, string>("param", "value"),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Add(new OperationOutcomeIssue(OperationOutcomeConstants.IssueSeverity.Warning, OperationOutcomeConstants.IssueType.Conflict));

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, null, false);

            await Assert.ThrowsAsync<BadRequestException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
        }

        [Fact]
        public async Task GivenBulkUpdateRequestWithALongUriSearchValue_WhenJobCreationRequested_ThenJobIsCreated()
        {
            var searchParams = new List<Tuple<string, string>>()
            {
                new Tuple<string, string>("_profile", "https://example.org/" + new string('b', 300)),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            OperationOutcomeIssue warning = LongUriSearchValueIssue.Create(new[] { "_profile" }, 256);
            _contextAccessor.RequestContext.BundleIssues.Add(warning);
            BulkUpdateDefinition enqueued = null;
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(args =>
            {
                enqueued = JsonConvert.DeserializeObject<BulkUpdateDefinition>(args.ArgAt<string[]>(1)[0]);
                return new List<JobInfo>
                {
                    new() { Id = 1 },
                };
            });

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, GenerateParameters("replace"), false);

            var response = await _handler.HandleAsync(request, CancellationToken.None);

            Assert.Equal(1, response.Id);
            await _queueClient.Received(1).EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>());
            Assert.Equal(new[] { warning.Diagnostics }, enqueued.SearchWarnings);
        }

        [Fact]
        public async Task GivenBulkUpdateRequestWithALongReferenceId_WhenJobCreationRequested_ThenJobIsCreated()
        {
            var searchParams = new List<Tuple<string, string>>()
            {
                new Tuple<string, string>("general-practitioner", "Practitioner/" + new string('a', 65)),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            OperationOutcomeIssue warning = LongReferenceIdIssue.Create("general-practitioner", 64);
            _contextAccessor.RequestContext.BundleIssues.Add(warning);
            BulkUpdateDefinition enqueued = null;
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(args =>
            {
                enqueued = JsonConvert.DeserializeObject<BulkUpdateDefinition>(args.ArgAt<string[]>(1)[0]);
                return new List<JobInfo>
                {
                    new() { Id = 1 },
                };
            });

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, GenerateParameters("replace"), false);

            var response = await _handler.HandleAsync(request, CancellationToken.None);

            Assert.Equal(1, response.Id);
            Assert.Equal(new[] { warning.Diagnostics }, enqueued.SearchWarnings);
        }

        [Theory]
        [InlineData("SearchParameter")]
        public async Task GivenBulkUpdateRequest_WhenResourceTypeIsExcluded_ThenBadRequestIsReturned(string resourceType)
        {
            var searchParams = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("param", "value"),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            var request = new CreateBulkUpdateRequest(resourceType, searchParams, null, false);

            var ex = await Assert.ThrowsAsync<BadRequestException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
            Assert.Equal($"Bulk update is not supported for resource type {resourceType}.", ex.Message);
        }

        [Fact]
        public async Task GivenBulkUpdateRequestAndEditProfileDefinitionsAccess_WhenJobCreated_ThenProfileModificationIsAllowed()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>())
                .Returns(DataActions.BulkOperator | DataActions.EditProfileDefinitions);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var definition = JsonConvert.DeserializeObject<BulkUpdateDefinition>(callInfo.ArgAt<string[]>(1)[0]);
                    Assert.True(definition.AllowProfileResourceModification);
                    return new List<JobInfo> { new() { Id = 1 } };
                });

            var request = new CreateBulkUpdateRequest("StructureDefinition", new List<Tuple<string, string>>(), GenerateParameters("replace"), false);

            CreateBulkUpdateResponse response = await _handler.HandleAsync(request, CancellationToken.None);

            Assert.Equal(1, response.Id);
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenMultipleOperationTypesAndOneIsUnsupported_ThenBadRequestIsReturned()
        {
            var searchParams = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("param", "value"),
            };

            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();

            // Parameters with two operation types: one supported, one unsupported
            var parameters = new Parameters
            {
                Parameter = new List<Parameters.ParameterComponent>
                {
                    new Parameters.ParameterComponent
                    {
                        Part = new List<Parameters.ParameterComponent>
                        {
                            new Parameters.ParameterComponent
                            {
                                Name = "type",
                                Value = new FhirString("Upsert"),
                            },
                        },
                    },
                    new Parameters.ParameterComponent
                    {
                        Part = new List<Parameters.ParameterComponent>
                        {
                            new Parameters.ParameterComponent
                            {
                                Name = "type",
                                Value = new FhirString("Delete"), // Not supported
                            },
                        },
                    },
                },
            };

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, parameters, false);
            await Assert.ThrowsAsync<BadRequestException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
        }

        [Theory]
        [InlineData(KnownResourceTypes.Basic, null, null, true)] // the topic resource type: every Basic on R4
        [InlineData(null, null, null, true)] // any resource type
        [InlineData(KnownResourceTypes.Patient, "_revinclude", "Basic:subject", true)]
        [InlineData(KnownResourceTypes.Patient, "_include:iterate", "Patient:link", true)]
        [InlineData(KnownResourceTypes.Patient, "name", "x", false)]
        public async Task GivenSubscriptionsEnabled_WhenABulkUpdateMayReachTopics_ThenTheSubscriptionTopicPermissionIsRequired(string resourceType, string parameter, string value, bool mayReachTopics)
        {
            // The caller is a bulk operator without the subscriptionTopic permission.
            _subscriptionsConfiguration.Enabled = true;
            _subscriptionModelConverter.IsSupported.Returns(true);
            _subscriptionModelConverter.TopicResourceType.Returns(KnownResourceTypes.Basic);
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>())
                .Returns(new List<JobInfo> { new() { Id = 1 } });
            var searchParams = parameter == null ? new List<Tuple<string, string>>() : new List<Tuple<string, string>> { Tuple.Create(parameter, value) };
            var request = new CreateBulkUpdateRequest(resourceType, searchParams, GenerateParameters("replace"), false);

            if (mayReachTopics)
            {
                await Assert.ThrowsAsync<UnauthorizedFhirActionException>(() => _handler.HandleAsync(request, CancellationToken.None));
            }
            else
            {
                Assert.Equal(1, (await _handler.HandleAsync(request, CancellationToken.None)).Id);
            }
        }

        [Theory]
        [InlineData(true, DataActions.BulkOperator | DataActions.SubscriptionTopic)] // enabled, with the permission
        [InlineData(false, DataActions.BulkOperator)] // disabled: topics are inert
        public async Task GivenABulkUpdateOfTheTopicResourceType_WhenThePermissionIsHeldOrSubscriptionsAreDisabled_ThenTheJobIsCreated(bool enabled, DataActions granted)
        {
            _subscriptionsConfiguration.Enabled = enabled;
            _subscriptionModelConverter.IsSupported.Returns(true);
            _subscriptionModelConverter.TopicResourceType.Returns(KnownResourceTypes.Basic);
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(granted);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>())
                .Returns(new List<JobInfo> { new() { Id = 1 } });

            CreateBulkUpdateResponse response = await _handler.HandleAsync(
                new CreateBulkUpdateRequest(KnownResourceTypes.Basic, new List<Tuple<string, string>>(), GenerateParameters("replace"), false),
                CancellationToken.None);

            Assert.Equal(1, response.Id);
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenUserIsUnauthorized_ThenUnauthorizedFhirActionExceptionIsThrown()
        {
            var searchParams = new List<Tuple<string, string>>
            {
                new Tuple<string, string>("param", "value"),
            };

            // Simulate user lacking BulkOperator permission
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.Write);

            Parameters parameters = GenerateParameters("upsert");

            var request = new CreateBulkUpdateRequest(KnownResourceTypes.Patient, searchParams, parameters, false);
            await Assert.ThrowsAsync<UnauthorizedFhirActionException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
        }

        [Fact]
        public async Task GivenBulkUpdateRequest_WhenJobCreationFails_ThenExceptionIsThrown()
        {
            _authorizationService.CheckAccess(Arg.Any<DataActions>(), Arg.Any<CancellationToken>()).Returns(DataActions.BulkOperator);
            _contextAccessor.RequestContext.BundleIssues.Clear();
            _queueClient.EnqueueAsync((byte)QueueType.BulkUpdate, Arg.Any<string[]>(), Arg.Any<long?>(), true, Arg.Any<CancellationToken>()).Returns(new List<JobInfo>());
            var parameters = GenerateParameters("upsert");

            var request = new CreateBulkUpdateRequest(null, new List<Tuple<string, string>>(), parameters, false);
            await Assert.ThrowsAsync<JobNotExistException>(async () => await _handler.HandleAsync(request, CancellationToken.None));
        }

        private static Parameters GenerateParameters(string typeValue)
        {
            return new Parameters
            {
                Parameter = new List<Parameters.ParameterComponent>
                {
                    new Parameters.ParameterComponent
                    {
                        Part = new List<Parameters.ParameterComponent>
                        {
                            new Parameters.ParameterComponent
                            {
                                Name = "type",
                                Value = new FhirString(typeValue),
                            },
                        },
                    },
                },
            };
        }

        public static IEnumerable<object[]> GetSearchParamsForJobCreation()
        {
            yield return new object[]
            {
                new List<Tuple<string, string>>
                {
                    new Tuple<string, string>("param", "value"),
                },
            };
            yield return new object[]
            {
                new List<Tuple<string, string>>
                {
                    new Tuple<string, string>("_lastUpdated", "value1"),
                    new Tuple<string, string>("_lastUpdated", "value2"),
                    new Tuple<string, string>("_lastUpdated", "value3"),
                },
            };
            yield return new object[]
            {
                new List<Tuple<string, string>>(),
            };
        }
    }
}
