// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Medino;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Messages.Create;
using Microsoft.Health.Fhir.Core.Messages.Delete;
using Microsoft.Health.Fhir.Core.Messages.Upsert;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Validates and authorizes API writes of subscriptions and topics, applies server-owned values, and captures the activation grant.
    /// Writes that bypass this pipeline (<c>$import</c>, bulk operations) produce no grant, so such subscriptions are never activated.
    /// </summary>
    public sealed class SubscriptionResourceBehavior :
        IPipelineBehavior<CreateResourceRequest, UpsertResourceResponse>,
        IPipelineBehavior<UpsertResourceRequest, UpsertResourceResponse>,
        IPipelineBehavior<DeleteResourceRequest, DeleteResourceResponse>,
        IPipelineBehavior<ConditionalDeleteResourceRequest, DeleteResourceResponse>
    {
        private static readonly HashSet<string> BaseResourceTypes = new(StringComparer.Ordinal) { KnownResourceTypes.Resource, KnownResourceTypes.DomainResource };

        private readonly SubscriptionsConfiguration _configuration;
        private readonly ISubscriptionModelConverter _converter;
        private readonly SubscriptionValidator _validator;
        private readonly ISubscriptionResourceLoader _loader;
        private readonly ISubscriptionStore _store;
        private readonly SubscriptionRegistry _registry;
        private readonly SubscriptionAuthorizer _authorizer;
        private readonly IAuthorizationService<DataActions> _authorizationService;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;

        public SubscriptionResourceBehavior(
            IOptions<SubscriptionsConfiguration> configuration,
            ISubscriptionModelConverter converter,
            SubscriptionValidator validator,
            ISubscriptionResourceLoader loader,
            ISubscriptionStore store,
            SubscriptionRegistry registry,
            SubscriptionAuthorizer authorizer,
            IAuthorizationService<DataActions> authorizationService,
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor)
        {
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _validator = EnsureArg.IsNotNull(validator, nameof(validator));
            _loader = EnsureArg.IsNotNull(loader, nameof(loader));
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _registry = EnsureArg.IsNotNull(registry, nameof(registry));
            _authorizer = EnsureArg.IsNotNull(authorizer, nameof(authorizer));
            _authorizationService = EnsureArg.IsNotNull(authorizationService, nameof(authorizationService));
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
        }

        private bool IsActive => _configuration.Enabled && _converter.IsSupported;

        public async Task<UpsertResourceResponse> HandleAsync(CreateResourceRequest request, RequestHandlerDelegate<UpsertResourceResponse> next, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(request, nameof(request));
            EnsureArg.IsNotNull(next, nameof(next));

            if (!IsActive)
            {
                return await next();
            }

            if (string.Equals(request.Resource.InstanceType, KnownResourceTypes.Subscription, StringComparison.Ordinal))
            {
                return await WriteSubscriptionAsync(request.Resource, existing: null, next, cancellationToken);
            }

            if (_converter.IsTopic(request.Resource))
            {
                await CheckTopicPermissionAsync(cancellationToken);
                ValidateTopic(request.Resource);
                _registry.Invalidate();
            }

            return await next();
        }

        public async Task<UpsertResourceResponse> HandleAsync(UpsertResourceRequest request, RequestHandlerDelegate<UpsertResourceResponse> next, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(request, nameof(request));
            EnsureArg.IsNotNull(next, nameof(next));

            if (!IsActive)
            {
                return await next();
            }

            ResourceElement resource = request.Resource;
            if (string.Equals(resource.InstanceType, KnownResourceTypes.Subscription, StringComparison.Ordinal))
            {
                ResourceElement existing = string.IsNullOrEmpty(resource.Id) ? null : await _loader.GetSubscriptionAsync(resource.Id, cancellationToken);
                return await WriteSubscriptionAsync(resource, existing, next, cancellationToken);
            }

            if (string.Equals(resource.InstanceType, _converter.TopicResourceType, StringComparison.Ordinal))
            {
                bool isTopic = _converter.IsTopic(resource);

                // The permission is needed when either the new or the stored resource is a topic, so a topic cannot be
                // turned into another Basic resource (or the reverse) without it.
                if (isTopic || await IsStoredTopicAsync(resource.InstanceType, resource.Id, cancellationToken))
                {
                    await CheckTopicPermissionAsync(cancellationToken);
                    _registry.Invalidate();
                }

                if (isTopic)
                {
                    ValidateTopic(resource);
                }
            }

            return await next();
        }

        public async Task<DeleteResourceResponse> HandleAsync(DeleteResourceRequest request, RequestHandlerDelegate<DeleteResourceResponse> next, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(request, nameof(request));
            EnsureArg.IsNotNull(next, nameof(next));

            if (!IsActive)
            {
                return await next();
            }

            string resourceType = request.ResourceKey.ResourceType;
            if (string.Equals(resourceType, _converter.TopicResourceType, StringComparison.Ordinal) &&
                await IsStoredTopicAsync(resourceType, request.ResourceKey.Id, cancellationToken))
            {
                await CheckTopicPermissionAsync(cancellationToken);
                _registry.Invalidate();
            }

            DeleteResourceResponse response = await next();

            if (string.Equals(resourceType, KnownResourceTypes.Subscription, StringComparison.Ordinal))
            {
                await _store.DeleteStateAsync(request.ResourceKey.Id, cancellationToken);
                _registry.Invalidate();
            }

            return response;
        }

        public async Task<DeleteResourceResponse> HandleAsync(ConditionalDeleteResourceRequest request, RequestHandlerDelegate<DeleteResourceResponse> next, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(request, nameof(request));
            EnsureArg.IsNotNull(next, nameof(next));

            // The matched resources are not known before the delete runs, so any conditional delete of the topic resource type needs
            // the permission. On R4 this applies to every conditional delete of Basic resources.
            if (IsActive && string.Equals(request.ResourceType, _converter.TopicResourceType, StringComparison.Ordinal))
            {
                await CheckTopicPermissionAsync(cancellationToken);
                _registry.Invalidate();
            }

            return await next();
        }

        private async Task<UpsertResourceResponse> WriteSubscriptionAsync(ResourceElement resource, ResourceElement existing, RequestHandlerDelegate<UpsertResourceResponse> next, CancellationToken cancellationToken)
        {
            SubscriptionInfo info;
            try
            {
                info = _converter.ToSubscription(resource);
            }
            catch (SubscriptionValidationException ex)
            {
                throw new BadRequestException(ex.Errors.ToList());
            }

            SubscriptionTopicInfo topic = await FindTopicAsync(info.TopicCanonical, cancellationToken);
            IReadOnlyList<string> errors = _validator.ValidateSubscription(info, topic);
            if (errors.Count > 0)
            {
                throw new BadRequestException(errors.ToList());
            }

            string contentHash = info.ComputeContentHash();
            SubscriptionGrant grant = _authorizer.CreateGrant(_requestContextAccessor.RequestContext, contentHash);
            foreach (string type in topic.ResourceTriggers.Select(t => SubscriptionMatcher.GetTypeName(t.Resource)).Distinct(StringComparer.Ordinal))
            {
                // Base types need access to every resource type, which SMART expresses with the "all" scope.
                string checkedType = BaseResourceTypes.Contains(type) ? "all" : type;
                if (!_authorizer.CanRead(grant, checkedType))
                {
                    throw new UnauthorizedFhirActionException();
                }
            }

            SubscriptionInfo existingInfo = TryConvert(existing);
            SubscriptionStatusCode status = DecideStatus(info, existingInfo, contentHash);
            if (existingInfo == null && status == SubscriptionStatusCode.Requested)
            {
                await EnsureCapacityAsync(cancellationToken);
            }

            _converter.ApplyServerValues(resource, status, CapEnd(info.End));

            UpsertResourceResponse response = await next();

            string id = response.Outcome.RawResourceElement.Id;
            await _store.SaveGrantAsync(id, contentHash, grant.Serialize(), cancellationToken);
            _registry.Invalidate();

            return response;
        }

        private static SubscriptionStatusCode DecideStatus(SubscriptionInfo requested, SubscriptionInfo existing, string contentHash)
        {
            // Clients may only request a handshake (requested) or stop a subscription (off, entered-in-error).
            // active and error are server-owned; they are kept only when nothing that affects delivery has changed.
            if (requested.Status is SubscriptionStatusCode.Off or SubscriptionStatusCode.EnteredInError)
            {
                return requested.Status;
            }

            if (existing != null &&
                requested.Status == existing.Status &&
                existing.Status is SubscriptionStatusCode.Active or SubscriptionStatusCode.Error &&
                string.Equals(existing.ComputeContentHash(), contentHash, StringComparison.Ordinal))
            {
                return existing.Status;
            }

            return SubscriptionStatusCode.Requested;
        }

        private DateTimeOffset? CapEnd(DateTimeOffset? end)
        {
            if (_configuration.MaxLifetimeDays <= 0)
            {
                return null;
            }

            DateTimeOffset max = Clock.UtcNow.AddDays(_configuration.MaxLifetimeDays);
            return end == null || end > max ? max : null;
        }

        private async Task EnsureCapacityAsync(CancellationToken cancellationToken)
        {
            SubscriptionRegistrySnapshot snapshot = await _registry.GetAsync(TimeSpan.FromSeconds(_configuration.RegistryRefreshIntervalSeconds), cancellationToken);
            int live = snapshot.Subscriptions.Count(s => s.Info?.Status is SubscriptionStatusCode.Requested or SubscriptionStatusCode.Active or SubscriptionStatusCode.Error);
            if (live >= _configuration.MaxActiveSubscriptions)
            {
                throw new BadRequestException(string.Format(System.Globalization.CultureInfo.InvariantCulture, Core.Resources.SubscriptionLimitReached, _configuration.MaxActiveSubscriptions));
            }
        }

        private async Task<SubscriptionTopicInfo> FindTopicAsync(string canonical, CancellationToken cancellationToken)
        {
            // Read topics directly rather than from the registry, so a topic created moments ago is found.
            var topics = new Dictionary<string, SubscriptionTopicInfo>(StringComparer.Ordinal);
            foreach (ResourceElement resource in await _loader.GetTopicsAsync(cancellationToken))
            {
                if (!_converter.IsTopic(resource))
                {
                    continue;
                }

                try
                {
                    SubscriptionTopicInfo topic = _converter.ToTopic(resource);
                    if (!string.IsNullOrEmpty(topic.Canonical))
                    {
                        topics[topic.Canonical] = topic;
                        if (!string.IsNullOrEmpty(topic.Version))
                        {
                            topics[topic.VersionedCanonical] = topic;
                        }
                    }
                }
                catch (SubscriptionValidationException)
                {
                    // An invalid stored topic cannot be subscribed to.
                }
            }

            return SubscriptionRegistry.ResolveTopic(topics, canonical);
        }

        private void ValidateTopic(ResourceElement resource)
        {
            SubscriptionTopicInfo topic;
            try
            {
                topic = _converter.ToTopic(resource);
            }
            catch (SubscriptionValidationException ex)
            {
                throw new BadRequestException(ex.Errors.ToList());
            }

            IReadOnlyList<string> errors = _validator.ValidateTopic(topic);
            if (errors.Count > 0)
            {
                throw new BadRequestException(errors.ToList());
            }
        }

        private async Task<bool> IsStoredTopicAsync(string resourceType, string id, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            if (!string.Equals(resourceType, KnownResourceTypes.Basic, StringComparison.Ordinal))
            {
                return string.Equals(resourceType, KnownResourceTypes.SubscriptionTopic, StringComparison.Ordinal);
            }

            ResourceElement stored = await _loader.GetResourceAsync(resourceType, id, cancellationToken);
            return stored != null && _converter.IsTopic(stored);
        }

        private async Task CheckTopicPermissionAsync(CancellationToken cancellationToken)
        {
            await _authorizationService.CheckAccess(DataActions.SubscriptionTopic, throwException: true, cancellationToken);
        }

        private SubscriptionInfo TryConvert(ResourceElement resource)
        {
            if (resource == null)
            {
                return null;
            }

            try
            {
                return _converter.ToSubscription(resource);
            }
            catch (SubscriptionValidationException)
            {
                return null;
            }
        }
    }
}
