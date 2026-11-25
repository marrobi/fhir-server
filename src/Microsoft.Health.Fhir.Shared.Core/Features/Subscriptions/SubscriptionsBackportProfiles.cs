// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// The Subscriptions R5 Backport IG 1.1.0 conformance resources the CapabilityStatement advertises on R4 and R4B: the
    /// <see cref="SubscriptionsBackport.SubscriptionProfile"/> profile, the extensions it uses and the value set it binds to.
    /// They ship with the server, so profile validation resolves the advertised profile without it being uploaded.
    /// </summary>
    public static class SubscriptionsBackportProfiles
    {
        private const string ResourcePrefix = "subscriptions-backport/";

        private static readonly Lazy<IReadOnlyList<Resource>> Resources = new(Load);

        /// <summary>
        /// Gets the built-in conformance resources for this FHIR version; empty except on R4 and R4B.
        /// </summary>
        public static IReadOnlyList<Resource> All => Resources.Value;

        /// <summary>
        /// Creates a resolver for the built-in conformance resources.
        /// </summary>
        /// <returns>The resolver.</returns>
        public static IResourceResolver CreateResolver() => new InMemoryResourceResolver(All);

        private static List<Resource> Load()
        {
            Assembly assembly = typeof(SubscriptionsBackportProfiles).Assembly;
            var parser = new FhirJsonParser();
            var resources = new List<Resource>();
            foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                using Stream stream = assembly.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                resources.Add(parser.Parse<Resource>(reader.ReadToEnd()));
            }

            // The published profiles include snapshots, so concurrent validations never generate one in the shared definition.
            return resources;
        }
    }
}
