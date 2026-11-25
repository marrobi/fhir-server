// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Features.FhirPath
{
    /// <summary>
    /// Bounded cache of compiled FHIRPath expressions shared by the FHIRPath providers.
    /// </summary>
    /// <remarks>
    /// Providers are called many times per indexed resource, so hits must not take locks or allocate; see
    /// <see cref="BoundedCache{TKey, TValue}"/> for the threading and eviction (first in, first out) behavior.
    /// Compile failures are not cached; they propagate to the caller.
    /// </remarks>
    internal sealed class CompiledFhirPathCache
    {
        /// <summary>
        /// The number of compiled expressions each provider keeps.
        /// </summary>
        internal const int DefaultCapacity = 4096;

        private readonly BoundedCache<string, ICompiledFhirPath> _cache;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompiledFhirPathCache"/> class.
        /// </summary>
        /// <param name="capacity">The maximum number of cached expressions.</param>
        /// <param name="compile">Compiles an expression that is not cached. It is called under the cache's mutation lock.</param>
        public CompiledFhirPathCache(int capacity, Func<string, ICompiledFhirPath> compile)
        {
            _cache = new BoundedCache<string, ICompiledFhirPath>(capacity, compile, StringComparer.Ordinal);
        }

        /// <summary>
        /// Returns the cached compiled expression, compiling and caching it on a miss.
        /// </summary>
        /// <param name="expression">The FHIRPath expression.</param>
        /// <returns>The compiled expression.</returns>
        public ICompiledFhirPath GetOrAdd(string expression) => _cache.GetOrAdd(expression);
    }
}
