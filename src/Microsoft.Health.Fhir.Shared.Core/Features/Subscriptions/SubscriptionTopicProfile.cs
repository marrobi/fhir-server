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
    /// The StructureDefinition for R4 subscription topics defined as <c>Basic</c> resources (<see cref="SubscriptionsBackport.R4TopicProfile"/>),
    /// and the cross-version <c>status</c> extension it uses as a modifier extension. They ship with the R4 server, so <c>$validate</c> can
    /// check topics without the profile or the extension being uploaded.
    /// </summary>
    public static class SubscriptionTopicProfile
    {
        private const string ResourceName = "subscription-topic-basic.json";

        // A validator must understand every modifier extension, so an unresolvable status extension is an error, not a warning.
        private const string StatusExtensionResourceName = "subscription-topic-status-extension.json";

        private static readonly Lazy<IReadOnlyList<StructureDefinition>> Definitions = new(() => Load(ResourceName));

        private static readonly Lazy<IReadOnlyList<StructureDefinition>> ExtensionDefinitions = new(() => Load(StatusExtensionResourceName));

        /// <summary>
        /// Gets the built-in profiles for this FHIR version; empty except on R4.
        /// </summary>
        public static IReadOnlyList<StructureDefinition> All => Definitions.Value;

        /// <summary>
        /// Creates a resolver for the built-in profiles.
        /// </summary>
        /// <returns>The resolver.</returns>
        public static IResourceResolver CreateResolver() => new InMemoryResourceResolver([.. All, .. ExtensionDefinitions.Value]);

        private static IReadOnlyList<StructureDefinition> Load(string resourceName)
        {
            using Stream stream = typeof(SubscriptionTopicProfile).Assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return [];
            }

            using var reader = new StreamReader(stream);
            return [new FhirJsonParser().Parse<StructureDefinition>(reader.ReadToEnd())];
        }
    }
}
