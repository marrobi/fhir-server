// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Security.Claims;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionAuthorizerTests : IClassFixture<SubscriptionTestFixture>
    {
        private readonly SubscriptionTestFixture _fixture;

        public SubscriptionAuthorizerTests(SubscriptionTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void GivenARequestContext_WhenCreatingAGrant_ThenIdentityRolesScopesAndCompartmentAreCaptured()
        {
            // Arrange
            SecurityConfiguration security = Security(new Role("reader", DataActions.Read, "/"));
            FhirRequestContext context = Context(roles: ["reader"]);
            context.AccessControlContext.ApplyFineGrainedAccessControl = true;
            context.AccessControlContext.AllowedResourceActions.Add(new ScopeRestriction("Observation", DataActions.Read, "patient"));
            context.AccessControlContext.CompartmentResourceType = "Patient";
            context.AccessControlContext.CompartmentId = "p1";

            // Act
            SubscriptionGrant grant = _fixture.CreateAuthorizer(security).CreateGrant(context, "hash");
            SubscriptionGrant roundTripped = SubscriptionGrant.Deserialize(grant.Serialize());

            // Assert
            Assert.Equal("hash", roundTripped.ContentHash);
            Assert.Equal("user-1", roundTripped.Subject);
            Assert.Equal(["reader"], roundTripped.Roles);
            Assert.True(roundTripped.Smart);
            Assert.Equal("Observation", Assert.Single(roundTripped.Scopes).Resource);
            Assert.Equal("p1", roundTripped.CompartmentId);
            Assert.Equal("https://fhir.example/", roundTripped.RequestBase);
        }

        [Fact]
        public void GivenARoleThatLaterLosesRead_WhenAuthorizing_ThenTheCurrentRoleConfigurationApplies()
        {
            var grant = new SubscriptionGrant { SecurityEnabled = true, RoleAuthorizationEnabled = true, Roles = ["reader"] };
            ResourceSnapshot observation = SubscriptionTestFixture.Snapshot(InMemorySearchEvaluatorTests.CreateObservation());

            Assert.True(_fixture.CreateAuthorizer(Security(new Role("reader", DataActions.Read, "/"))).IsAuthorized(grant, observation));
            Assert.False(_fixture.CreateAuthorizer(Security(new Role("reader", DataActions.Write, "/"))).IsAuthorized(grant, observation));
            Assert.False(_fixture.CreateAuthorizer(Security()).IsAuthorized(grant, observation));
        }

        [Fact]
        public void GivenRolesLoadedIntoTheRegisteredAuthorizationConfiguration_WhenCheckingAccess_ThenThoseRolesApply()
        {
            // Arrange: the development identity provider registers its own AuthorizationConfiguration, and RoleLoader fills that instance.
            SecurityConfiguration security = Security();
            var registered = new AuthorizationConfiguration { Enabled = true, Roles = [new Role("reader", DataActions.Read, "/")] };
            var authorizer = new SubscriptionAuthorizer(Options.Create(security), _fixture.SearchEvaluator, registered);
            SubscriptionGrant grant = authorizer.CreateGrant(Context(roles: ["reader"]), "hash");

            // Act
            bool canRead = authorizer.CanRead(grant, "Observation");
            bool authorized = authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(InMemorySearchEvaluatorTests.CreateObservation()));

            // Assert
            Assert.True(canRead);
            Assert.True(authorized);
        }

        [Fact]
        public void GivenAGrantCapturedWithSecurityOff_WhenSecurityIsTurnedOn_ThenNothingIsAuthorized()
        {
            var grant = new SubscriptionGrant { SecurityEnabled = false };
            ResourceSnapshot observation = SubscriptionTestFixture.Snapshot(InMemorySearchEvaluatorTests.CreateObservation());

            Assert.True(_fixture.CreateAuthorizer().IsAuthorized(grant, observation));
            Assert.False(_fixture.CreateAuthorizer(Security(new Role("reader", DataActions.Read, "/"))).IsAuthorized(grant, observation));
        }

        [Fact]
        public void GivenSmartScopes_WhenAuthorizing_ThenOnlyScopedResourceTypesAreAllowed()
        {
            var grant = new SubscriptionGrant { Smart = true, Scopes = [new SubscriptionGrantScope { Resource = "Observation", DataActions = DataActions.Read }] };
            SubscriptionAuthorizer authorizer = _fixture.CreateAuthorizer();

            Assert.True(authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(InMemorySearchEvaluatorTests.CreateObservation())));
            Assert.False(authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(new Patient { Id = "p1" })));
            Assert.True(authorizer.CanRead(grant, "Observation"));
            Assert.False(authorizer.CanRead(grant, "Patient"));
        }

        [Fact]
        public void GivenAWriteOnlySmartScope_WhenAuthorizing_ThenReadIsNotGranted()
        {
            var grant = new SubscriptionGrant { Smart = true, Scopes = [new SubscriptionGrantScope { Resource = "all", DataActions = DataActions.Write }] };

            Assert.False(_fixture.CreateAuthorizer().CanRead(grant, "Observation"));
        }

        [Fact]
        public void GivenAFineGrainedSmartScope_WhenAuthorizing_ThenItsSearchRestrictionIsEvaluated()
        {
            var grant = new SubscriptionGrant
            {
                Smart = true,
                Scopes = [new SubscriptionGrantScope { Resource = "Observation", DataActions = DataActions.Read, SearchParameters = [new KeyValuePair<string, string>("status", "final")] }],
            };
            Observation preliminary = InMemorySearchEvaluatorTests.CreateObservation();
            preliminary.Status = ObservationStatus.Preliminary;
            SubscriptionAuthorizer authorizer = _fixture.CreateAuthorizer();

            Assert.True(authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(InMemorySearchEvaluatorTests.CreateObservation())));
            Assert.False(authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(preliminary)));
        }

        [Fact]
        public void GivenACompartmentGrant_WhenAuthorizing_ThenOnlyResourcesInTheCompartmentAreAllowed()
        {
            var grant = new SubscriptionGrant { CompartmentResourceType = "Patient", CompartmentId = "p1" };
            Observation other = InMemorySearchEvaluatorTests.CreateObservation();
            other.Subject = new ResourceReference("Patient/p2");
            SubscriptionAuthorizer authorizer = _fixture.CreateAuthorizer();

            Assert.True(authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(InMemorySearchEvaluatorTests.CreateObservation())));
            Assert.False(authorizer.IsAuthorized(grant, SubscriptionTestFixture.Snapshot(other)));
        }

        private static SecurityConfiguration Security(params Role[] roles)
        {
            var security = new SecurityConfiguration { Enabled = true };
            security.Authorization.Enabled = true;
            security.Authorization.Roles = roles;
            return security;
        }

        private static FhirRequestContext Context(string[] roles)
        {
            var claims = new List<System.Security.Claims.Claim> { new("oid", "user-1") };
            foreach (string role in roles)
            {
                claims.Add(new System.Security.Claims.Claim("roles", role));
            }

            return new FhirRequestContext("POST", "https://fhir.example/Subscription", "https://fhir.example/", Guid.NewGuid().ToString(), new Dictionary<string, StringValues>(), new Dictionary<string, StringValues>())
            {
                Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
            };
        }
    }
}
