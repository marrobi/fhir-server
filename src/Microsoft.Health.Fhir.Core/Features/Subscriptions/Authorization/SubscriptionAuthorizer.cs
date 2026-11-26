// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization
{
    /// <summary>
    /// Captures subscription grants and checks events against them. There is no live request when a notification is sent,
    /// so every check is made against the grant, re-evaluating local role assignments against the current role configuration.
    /// </summary>
    public sealed class SubscriptionAuthorizer
    {
        private const string AllResources = "all";

        // A subscription reports every matching resource, so it is a standing search: ReadById (SMART v2 ".r") is not enough.
        private const DataActions ReadActions = DataActions.Read | DataActions.Search;

        private readonly SecurityConfiguration _securityConfiguration;
        private readonly AuthorizationConfiguration _authorizationConfiguration;
        private readonly InMemorySearchEvaluator _searchEvaluator;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionAuthorizer"/> class.
        /// </summary>
        /// <param name="securityConfiguration">The security configuration.</param>
        /// <param name="searchEvaluator">The in-memory search evaluator used for compartment and scope checks.</param>
        /// <param name="authorizationConfiguration">
        /// The registered authorization configuration, which is the instance <see cref="RoleLoader"/> populates with roles.
        /// It can be a different instance from <see cref="SecurityConfiguration.Authorization"/>, which is used when none is registered.
        /// </param>
        public SubscriptionAuthorizer(IOptions<SecurityConfiguration> securityConfiguration, InMemorySearchEvaluator searchEvaluator, AuthorizationConfiguration authorizationConfiguration = null)
        {
            _securityConfiguration = EnsureArg.IsNotNull(securityConfiguration?.Value, nameof(securityConfiguration));
            _searchEvaluator = EnsureArg.IsNotNull(searchEvaluator, nameof(searchEvaluator));
            _authorizationConfiguration = authorizationConfiguration ?? _securityConfiguration.Authorization;
        }

        /// <summary>
        /// Captures the caller's authorization as a grant bound to <paramref name="contentHash"/>.
        /// </summary>
        /// <param name="requestContext">The current request context.</param>
        /// <param name="contentHash">The subscription content hash.</param>
        /// <returns>The grant.</returns>
        public SubscriptionGrant CreateGrant(IFhirRequestContext requestContext, string contentHash)
        {
            EnsureArg.IsNotNull(requestContext, nameof(requestContext));
            EnsureArg.IsNotNullOrWhiteSpace(contentHash, nameof(contentHash));

            ClaimsPrincipal principal = requestContext.Principal;
            AccessControlContext accessControl = requestContext.AccessControlContext ?? new AccessControlContext();
            string rolesClaim = _authorizationConfiguration.RolesClaim;

            return new SubscriptionGrant
            {
                ContentHash = contentHash,
                SecurityEnabled = _securityConfiguration.Enabled,
                RoleAuthorizationEnabled = _securityConfiguration.Enabled && _securityConfiguration.Authorization.Enabled,
                Subject = principal?.FindFirst("oid")?.Value ?? principal?.FindFirst("sub")?.Value ?? principal?.Identity?.Name,
                Roles = principal?.FindAll(rolesClaim).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
                Smart = accessControl.ApplyFineGrainedAccessControl,
                Scopes = accessControl.AllowedResourceActions.Select(s => new SubscriptionGrantScope
                {
                    Resource = s.Resource,
                    DataActions = s.AllowedDataAction,
                    SearchParameters = s.SearchParameters?.Parameters?.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)).ToList() ?? [],
                }).ToList(),
                CompartmentResourceType = accessControl.CompartmentResourceType,
                CompartmentId = accessControl.CompartmentId,
                RequestBase = requestContext.BaseUri?.ToString(),
                CreatedUtc = Clock.UtcNow,
            };
        }

        /// <summary>
        /// Returns true when the grant allows reading resources of <paramref name="resourceType"/> at all.
        /// Used when a subscription is created, to reject topics the caller could never be notified about.
        /// </summary>
        /// <param name="grant">The grant.</param>
        /// <param name="resourceType">The resource type.</param>
        /// <returns>Whether reading the type may be allowed.</returns>
        public bool CanRead(SubscriptionGrant grant, string resourceType)
        {
            EnsureArg.IsNotNull(grant, nameof(grant));

            return SecurityModeMatches(grant) && RolesAllowRead(grant) && (!grant.Smart || ApplicableScopes(grant, resourceType).Any());
        }

        /// <summary>
        /// Returns true when the grant allows the subscriber to know about this resource version.
        /// </summary>
        /// <param name="grant">The grant.</param>
        /// <param name="snapshot">The resource version.</param>
        /// <returns>Whether the event may be numbered and sent.</returns>
        public bool IsAuthorized(SubscriptionGrant grant, ResourceSnapshot snapshot)
        {
            EnsureArg.IsNotNull(grant, nameof(grant));
            EnsureArg.IsNotNull(snapshot, nameof(snapshot));

            if (!SecurityModeMatches(grant) || !RolesAllowRead(grant))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(grant.CompartmentResourceType) && !string.IsNullOrEmpty(grant.CompartmentId) &&
                !_searchEvaluator.IsInCompartment(snapshot, grant.CompartmentResourceType, grant.CompartmentId))
            {
                return false;
            }

            if (!grant.Smart)
            {
                return true;
            }

            foreach (SubscriptionGrantScope scope in ApplicableScopes(grant, snapshot.ResourceType))
            {
                if (scope.SearchParameters.Count == 0)
                {
                    return true;
                }

                try
                {
                    if (_searchEvaluator.Matches(snapshot, _searchEvaluator.Parse(snapshot.ResourceType, scope.SearchParameters)))
                    {
                        return true;
                    }
                }
                catch (SubscriptionCriteriaException)
                {
                    // A scope restriction that cannot be evaluated in memory does not grant access.
                }
            }

            return false;
        }

        private bool SecurityModeMatches(SubscriptionGrant grant)
        {
            // A grant captured with security off must not authorize anything once security is on.
            if (_securityConfiguration.Enabled && !grant.SecurityEnabled)
            {
                return false;
            }

            bool roleAuthorization = _securityConfiguration.Enabled && _securityConfiguration.Authorization.Enabled;
            return !roleAuthorization || grant.RoleAuthorizationEnabled;
        }

        private bool RolesAllowRead(SubscriptionGrant grant)
        {
            if (!(_securityConfiguration.Enabled && _securityConfiguration.Authorization.Enabled))
            {
                return true;
            }

            DataActions permitted = DataActions.None;
            foreach (string roleName in grant.Roles)
            {
                Role role = _authorizationConfiguration.Roles.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.OrdinalIgnoreCase));
                if (role != null)
                {
                    permitted |= role.AllowedDataActions;
                }
            }

            return (permitted & ReadActions) != DataActions.None;
        }

        private static IEnumerable<SubscriptionGrantScope> ApplicableScopes(SubscriptionGrant grant, string resourceType)
        {
            return grant.Scopes.Where(s =>
                (string.Equals(s.Resource, resourceType, StringComparison.Ordinal) || string.Equals(s.Resource, AllResources, StringComparison.OrdinalIgnoreCase)) &&
                (s.DataActions & ReadActions) != DataActions.None);
        }
    }
}
