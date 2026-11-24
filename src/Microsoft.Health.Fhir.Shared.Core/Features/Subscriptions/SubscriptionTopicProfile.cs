// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// The StructureDefinition for R4 subscription topics defined as <c>Basic</c> resources (<see cref="SubscriptionsBackport.R4TopicProfile"/>).
    /// It ships with the R4 server, so <c>$validate</c> can check topics against it without the profile being uploaded.
    /// </summary>
    public static class SubscriptionTopicProfile
    {
        private const string ResourceName = "subscription-topic-basic.json";

        private static readonly Lazy<IReadOnlyList<StructureDefinition>> Definitions = new(Load);

        /// <summary>
        /// Gets the built-in profiles for this FHIR version; empty except on R4.
        /// </summary>
        public static IReadOnlyList<StructureDefinition> All => Definitions.Value;

        /// <summary>
        /// Creates a resolver for the built-in profiles.
        /// </summary>
        /// <returns>The resolver.</returns>
        public static IResourceResolver CreateResolver() => new InMemoryResourceResolver(All);

        private static IReadOnlyList<StructureDefinition> Load()
        {
            using Stream stream = typeof(SubscriptionTopicProfile).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                return [];
            }

            using var reader = new StreamReader(stream);
            return [new FhirJsonParser().Parse<StructureDefinition>(reader.ReadToEnd())];
        }
    }
}
