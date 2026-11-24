// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Compartment;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.FhirPath;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Validation.FhirPrimitiveTypes;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Features.Search;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// Builds the real search stack (definitions, indexer, expression parser, compartments) once for subscription tests.
    /// </summary>
    public sealed class SubscriptionTestFixture : IAsyncLifetime
    {
        public InMemorySearchEvaluator SearchEvaluator { get; private set; }

        public FhirPathCriteriaEvaluator FhirPathEvaluator { get; private set; }

        public SubscriptionMatcher Matcher { get; private set; }

        public async ValueTask InitializeAsync()
        {
            SearchParameterDefinitionManager definitions = await new SearchParameterFixtureData().GetSearchDefinitionManagerAsync();
            var contextAccessor = new FhirRequestContextAccessor();
            var referenceParser = Mock.TypeWithArguments<ReferenceSearchValueParser>(contextAccessor, ResourceIdPolicy.Standard);

            var supported = new SupportedSearchParameterDefinitionManager(definitions);
            var indexer = new TypedElementSearchIndexer(
                supported,
                await SearchParameterFixtureData.GetFhirTypedElementToSearchValueConverterManagerAsync(),
                new LightweightReferenceToElementResolver(referenceParser, ModelInfoProvider.Instance),
                ModelInfoProvider.Instance,
                new FirelyFhirPathProvider(),
                NullLogger<TypedElementSearchIndexer>.Instance);

            var expressionParser = new ExpressionParser(() => definitions, new SearchParameterExpressionParser(referenceParser));

            var compartmentDefinitions = new CompartmentDefinitionManager(ModelInfoProvider.Instance);
            await compartmentDefinitions.StartAsync(CancellationToken.None);
            var compartmentIndexer = new CompartmentIndexer(compartmentDefinitions);

            SearchEvaluator = new InMemorySearchEvaluator(expressionParser, indexer, compartmentIndexer);
            FhirPathEvaluator = new FhirPathCriteriaEvaluator(new FirelyFhirPathProvider());
            Matcher = new SubscriptionMatcher(SearchEvaluator, FhirPathEvaluator);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public SubscriptionValidator CreateValidator(SubscriptionsConfiguration configuration = null)
        {
            IOptions<SubscriptionsConfiguration> options = Options.Create(configuration ?? new SubscriptionsConfiguration());
            var channel = Substitute.For<ISubscriptionChannel>();
            channel.ChannelType.Returns("rest-hook");
            return new SubscriptionValidator(SearchEvaluator, FhirPathEvaluator, new SubscriptionEndpointPolicy(options), options, ModelInfoProvider.Instance, [channel]);
        }

        public SubscriptionAuthorizer CreateAuthorizer(SecurityConfiguration security = null)
        {
            return new SubscriptionAuthorizer(Options.Create(security ?? new SecurityConfiguration()), SearchEvaluator);
        }

        public static ResourceSnapshot Snapshot(Resource resource) => new(resource.ToResourceElement());

        public static ResourceChange Change(Resource current, Resource previous = null, bool deleted = false, int? version = null, long transactionId = 100, string type = null, string id = null)
        {
            int v = version ?? (previous == null ? 1 : int.Parse(previous.Meta?.VersionId ?? "1") + 1);
            Resource typed = current ?? previous;
            return new ResourceChange
            {
                TransactionId = transactionId,
                ResourceTypeId = 1,
                ResourceSurrogateId = transactionId,
                ResourceType = type ?? typed.TypeName,
                ResourceId = id ?? typed.Id,
                Version = v,
                IsDeleted = deleted,
                LastModified = DateTimeOffset.UtcNow,
                Current = current == null ? null : Snapshot(current),
                Previous = previous == null ? null : Snapshot(previous),
                PreviousLoaded = v > 1,
            };
        }
    }
}
