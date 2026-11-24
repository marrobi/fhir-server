// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// One version of a resource, with its search and compartment indices computed on first use.
    /// </summary>
    public sealed class ResourceSnapshot
    {
        public ResourceSnapshot(ResourceElement resource)
        {
            Resource = EnsureArg.IsNotNull(resource, nameof(resource));
        }

        public ResourceElement Resource { get; }

        public string ResourceType => Resource.InstanceType;

        public string ResourceId => Resource.Id;

        internal IReadOnlyCollection<SearchIndexEntry> SearchIndices { get; set; }

        internal CompartmentIndices CompartmentIndices { get; set; }
    }
}
