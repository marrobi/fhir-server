// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EnsureThat;
using Medino;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Api.Features.Audit;
using Microsoft.Health.Fhir.Api.Features.ActionResults;
using Microsoft.Health.Fhir.Api.Features.Filters;
using Microsoft.Health.Fhir.Core.Features.Routing;
using Microsoft.Health.Fhir.Core.Messages.Subscriptions;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Api.Controllers
{
    /// <summary>
    /// The Subscription $status operation (R5; Subscriptions R5 Backport IG for R4 and R4B).
    /// </summary>
    [ServiceFilter(typeof(AuditLoggingFilterAttribute))]
    [ServiceFilter(typeof(OperationOutcomeExceptionFilterAttribute))]
    [ServiceFilter(typeof(ValidateFormatParametersAttribute))]
    [ValidateModelState]
    public class SubscriptionController : Controller
    {
        private const string IdParameter = "id";

        private readonly IMediator _mediator;
        private readonly ILogger<SubscriptionController> _logger;

        public SubscriptionController(IMediator mediator, ILogger<SubscriptionController> logger)
        {
            _mediator = EnsureArg.IsNotNull(mediator, nameof(mediator));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <summary>
        /// Returns the status of one subscription.
        /// </summary>
        /// <param name="idParameter">The subscription id.</param>
        /// <returns>A searchset bundle with the subscription status.</returns>
        [HttpGet]
        [HttpPost]
        [Route(KnownRoutes.SubscriptionStatusById, Name = RouteNames.SubscriptionStatusById)]
        [AuditEventType(AuditEventSubType.SubscriptionStatus)]
        public async Task<IActionResult> StatusById(string idParameter)
        {
            SubscriptionStatusResponse response = await _mediator.SendAsync(new SubscriptionStatusRequest([idParameter]), HttpContext.RequestAborted);
            return FhirResult.Create(_logger, response.Bundle);
        }

        /// <summary>
        /// Returns the status of the subscriptions given by the <c>id</c> parameter.
        /// </summary>
        /// <returns>A searchset bundle with the subscription statuses.</returns>
        [HttpGet]
        [Route(KnownRoutes.SubscriptionStatus, Name = RouteNames.SubscriptionStatus)]
        [AuditEventType(AuditEventSubType.SubscriptionStatus)]
        public async Task<IActionResult> Status()
        {
            IReadOnlyList<string> ids = Request.Query[IdParameter]
                .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            SubscriptionStatusResponse response = await _mediator.SendAsync(new SubscriptionStatusRequest(ids), HttpContext.RequestAborted);
            return FhirResult.Create(_logger, response.Bundle);
        }
    }
}
