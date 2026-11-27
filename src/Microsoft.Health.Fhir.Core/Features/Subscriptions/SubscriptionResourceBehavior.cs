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
        IPipelineBehavior<DeleteResourceRequest, DeleteResourceResponse>
    {
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

            // Deleting a topic is checked by the deletion service, which every delete reaches, including conditional deletes.
            DeleteResourceResponse response = await next();

            // $purge-history keeps the current version, so the subscription and its state remain.
            if (string.Equals(resourceType, KnownResourceTypes.Subscription, StringComparison.Ordinal) &&
                request.DeleteOperation != DeleteOperation.PurgeHistory &&
                _store.Availability == SubscriptionStoreAvailability.Available)
            {
                await _store.DeleteStateAsync(request.ResourceKey.Id, cancellationToken);
                _registry.Invalidate();
            }

            return response;
        }

        private async Task<UpsertResourceResponse> WriteSubscriptionAsync(ResourceElement resource, ResourceElement existing, RequestHandlerDelegate<UpsertResourceResponse> next, CancellationToken cancellationToken)
        {
            switch (_store.Availability)
            {
                case SubscriptionStoreAvailability.NotSupported:
                    throw new OperationNotImplementedException(Core.Resources.SubscriptionsRequireSqlServer);
                case SubscriptionStoreAvailability.NotReady:
                    // For example, while the database schema is being upgraded; retrying later succeeds.
                    throw new ServiceUnavailableException();
            }

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

            // A stopped subscription keeps no grant and receives nothing, so the caller's read access to the topic's types is only
            // checked when it is requested. Its topic may also be missing.
            IEnumerable<TopicResourceTrigger> triggers = info.Status.IsLive() ? topic.ResourceTriggers : [];
            foreach (string type in triggers.Select(t => SubscriptionMatcher.GetTypeName(t.Resource)).Distinct(StringComparer.Ordinal))
            {
                // Base types need access to every resource type, which SMART expresses with the "all" scope.
                string checkedType = SubscriptionMatcher.IsBaseType(type) ? "all" : type;
                if (!_authorizer.CanRead(grant, checkedType))
                {
                    throw new UnauthorizedFhirActionException();
                }
            }

            SubscriptionInfo existingInfo = TryConvert(existing);

            // Writes that leave the content unchanged keep the grant of the identity that last set the content, unless the same identity
            // renews it. Otherwise a routine write by a more privileged identity would widen what an existing endpoint receives.
            SubscriptionGrant effectiveGrant = await GetRetainedGrantAsync(existing?.Id, contentHash, grant.Subject, cancellationToken) ?? grant;

            SubscriptionStatusCode status = DecideStatus(info, existingInfo, contentHash);
            if (status.IsLive() && (existingInfo == null || !existingInfo.Status.IsLive()))
            {
                // Any transition into a live status counts, so creating as off and then requesting cannot exceed the limit.
                await EnsureCapacityAsync(cancellationToken);
            }

            _converter.ApplyServerValues(resource, status, CapEnd(info.End, effectiveGrant.CreatedUtc));

            UpsertResourceResponse response = await next();

            string id = response.Outcome.RawResourceElement.Id;
            if (status.IsLive())
            {
                await _store.SaveGrantAsync(id, contentHash, effectiveGrant.Serialize(), cancellationToken);
            }
            else
            {
                // A stopped subscription keeps no grant, so writing it back (for example with $import) cannot reactivate it.
                await _store.DeactivateAsync(id, revokeGrant: true, cancellationToken);
            }

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

        private async Task<SubscriptionGrant> GetRetainedGrantAsync(string id, string contentHash, string callerSubject, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            SubscriptionState state = await _store.GetStateAsync(id, cancellationToken);
            if (string.IsNullOrEmpty(state?.Grant) || !string.Equals(state.ContentHash, contentHash, StringComparison.Ordinal))
            {
                return null;
            }

            SubscriptionGrant stored = SubscriptionGrant.Deserialize(state.Grant);
            return string.Equals(stored.Subject, callerSubject, StringComparison.Ordinal) ? null : stored;
        }

        private DateTimeOffset? CapEnd(DateTimeOffset? end, DateTimeOffset grantCreatedUtc)
        {
            if (_configuration.MaxLifetimeDays <= 0)
            {
                return null;
            }

            // The lifetime counts from when the grant was captured, so a write that keeps an older grant cannot extend it.
            DateTimeOffset max = grantCreatedUtc.AddDays(_configuration.MaxLifetimeDays);
            return end == null || end > max ? max : null;
        }

        private async Task EnsureCapacityAsync(CancellationToken cancellationToken)
        {
            SubscriptionRegistrySnapshot snapshot = await _registry.GetAsync(TimeSpan.FromSeconds(_configuration.RegistryRefreshIntervalSeconds), cancellationToken);
            int live = snapshot.Subscriptions.Count(s => s.Info?.Status.IsLive() == true);
            if (live >= _configuration.MaxActiveSubscriptions)
            {
                throw new BadRequestException(string.Format(System.Globalization.CultureInfo.InvariantCulture, Core.Resources.SubscriptionLimitReached, _configuration.MaxActiveSubscriptions));
            }
        }

        private async Task<SubscriptionTopicInfo> FindTopicAsync(string canonical, CancellationToken cancellationToken)
        {
            // Read topics directly rather than from the registry, so a topic created moments ago is found.
            Dictionary<string, SubscriptionTopicInfo> topics = _registry.BuildTopicMap(await _loader.GetTopicsAsync(cancellationToken));
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
