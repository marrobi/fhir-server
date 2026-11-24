// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// A filter a topic allows subscribers to apply.
    /// </summary>
    public sealed class TopicCanFilterBy
    {
        public string Description { get; init; }

        public string Resource { get; init; }

        public string FilterParameter { get; init; }

        public IReadOnlyList<string> Comparators { get; init; } = [];

        public IReadOnlyList<string> Modifiers { get; init; } = [];
    }
}
