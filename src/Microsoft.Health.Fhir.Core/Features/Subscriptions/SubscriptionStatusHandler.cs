// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Medino;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Messages.Get;
using Microsoft.Health.Fhir.Core.Messages.Subscriptions;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Handles the Subscription $status operation (R5, and the Backport IG for R4 and R4B).
    /// </summary>
    public sealed class SubscriptionStatusHandler : IRequestHandler<SubscriptionStatusRequest, SubscriptionStatusResponse>
    {
        private readonly IAuthorizationService<DataActions> _authorizationService;
        private readonly IMediator _mediator;
        private readonly ISubscriptionResourceLoader _loader;
        private readonly ISubscriptionModelConverter _converter;
        private readonly ISubscriptionStore _store;
        private readonly ISubscriptionNotificationFactory _notificationFactory;
        private readonly SubscriptionsConfiguration _configuration;
        private readonly RequestContextAccessor<IFhirRequestContext> _requestContextAccessor;

        public SubscriptionStatusHandler(
            IAuthorizationService<DataActions> authorizationService,
            IMediator mediator,
            ISubscriptionResourceLoader loader,
            ISubscriptionModelConverter converter,
            ISubscriptionStore store,
            ISubscriptionNotificationFactory notificationFactory,
            IOptions<SubscriptionsConfiguration> configuration,
            RequestContextAccessor<IFhirRequestContext> requestContextAccessor)
        {
            _authorizationService = EnsureArg.IsNotNull(authorizationService, nameof(authorizationService));
            _mediator = EnsureArg.IsNotNull(mediator, nameof(mediator));
            _loader = EnsureArg.IsNotNull(loader, nameof(loader));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _notificationFactory = EnsureArg.IsNotNull(notificationFactory, nameof(notificationFactory));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _requestContextAccessor = EnsureArg.IsNotNull(requestContextAccessor, nameof(requestContextAccessor));
        }

        public async Task<SubscriptionStatusResponse> HandleAsync(SubscriptionStatusRequest request, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(request, nameof(request));

            await _authorizationService.CheckAccess(DataActions.Read, true, cancellationToken);

            if (!_configuration.Enabled || !_converter.IsSupported)
            {
                throw new OperationNotImplementedException(Core.Resources.SubscriptionsNotEnabled);
            }

            if (request.SubscriptionIds.Count == 0)
            {
                throw new RequestNotValidException(Core.Resources.SubscriptionStatusIdRequired);
            }

            var statuses = new List<SubscriptionNotificationRequest>(request.SubscriptionIds.Count);
            foreach (string id in request.SubscriptionIds)
            {
                // Reading through the request pipeline also applies resource-level authorization, including SMART scopes.
                await _mediator.SendAsync(new GetResourceRequest(KnownResourceTypes.Subscription, id), cancellationToken);

                ResourceElement resource = await _loader.GetSubscriptionAsync(id, cancellationToken)
                    ?? throw new ResourceNotFoundException(string.Format(CultureInfo.InvariantCulture, Core.Resources.ResourceNotFoundById, KnownResourceTypes.Subscription, id));

                SubscriptionInfo info;
                try
                {
                    info = _converter.ToSubscription(resource);
                }
                catch (SubscriptionValidationException ex)
                {
                    info = new SubscriptionInfo { Id = id, Status = SubscriptionStatusCode.Error, Error = ex.Message };
                }

                SubscriptionState state = await _store.GetStateAsync(id, cancellationToken);
                SubscriptionEventCounts counts = await _store.GetEventCountsAsync(id, cancellationToken);

                var errors = new List<string>();
                string error = state?.Error ?? info.Error;
                if (!string.IsNullOrEmpty(error))
                {
                    errors.Add(error);
                }

                if (counts.Unevaluable > 0)
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionUnevaluableEvents, counts.Unevaluable));
                }

                statuses.Add(new SubscriptionNotificationRequest
                {
                    Subscription = info,
                    Type = SubscriptionNotificationType.QueryStatus,
                    Status = info.Status,
                    EventsSinceSubscriptionStart = state?.LastEventNumber ?? 0,
                    Errors = errors,
                    BaseUri = _requestContextAccessor.RequestContext?.BaseUri,
                });
            }

            return new SubscriptionStatusResponse(_notificationFactory.CreateStatusBundle(statuses));
        }
    }
}
