// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EnsureThat;
using Hl7.Fhir.ElementModel;
using Hl7.FhirPath;
using Microsoft.Health.Fhir.Core.Features.FhirPath;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// Evaluates topic <c>fhirPathCriteria</c> with the R5 <c>%previous</c> and <c>%current</c> variables.
    /// </summary>
    public sealed class FhirPathCriteriaEvaluator
    {
        private readonly IFhirPathProvider _fhirPathProvider;
        private readonly ConcurrentDictionary<string, ICompiledFhirPath> _cache = new(StringComparer.Ordinal);

        public FhirPathCriteriaEvaluator(IFhirPathProvider fhirPathProvider)
        {
            _fhirPathProvider = EnsureArg.IsNotNull(fhirPathProvider, nameof(fhirPathProvider));
        }

        /// <summary>
        /// Gets a value indicating whether the expression refers to the previous version.
        /// </summary>
        /// <param name="expression">The FHIRPath expression.</param>
        /// <returns>Whether %previous is used.</returns>
        public static bool UsesPrevious(string expression) =>
            !string.IsNullOrEmpty(expression) && expression.Contains("%previous", StringComparison.Ordinal);

        /// <summary>
        /// Compiles the expression, throwing <see cref="SubscriptionCriteriaException"/> when it is invalid.
        /// </summary>
        /// <param name="expression">The FHIRPath expression.</param>
        public void Validate(string expression)
        {
            Compile(expression);
        }

        /// <summary>
        /// Evaluates the expression. The result must be a single boolean <c>true</c> for the criterion to pass.
        /// </summary>
        /// <param name="expression">The FHIRPath expression.</param>
        /// <param name="previous">The previous version, or null for a create.</param>
        /// <param name="current">The changed version, or null.</param>
        /// <returns>Whether the criterion passes.</returns>
        public bool Evaluate(string expression, ResourceSnapshot previous, ResourceSnapshot current)
        {
            ICompiledFhirPath compiled = Compile(expression);
            ITypedElement focus = (current ?? previous)?.Resource.Instance;
            if (focus == null)
            {
                return false;
            }

            var context = new EvaluationContext();
            context.Environment["previous"] = previous == null ? Enumerable.Empty<ITypedElement>() : [previous.Resource.Instance];
            context.Environment["current"] = current == null ? Enumerable.Empty<ITypedElement>() : [current.Resource.Instance];

            List<ITypedElement> result = compiled.Select(focus, context).ToList();
            return result.Count == 1 && result[0].Value is bool b && b;
        }

        private ICompiledFhirPath Compile(string expression)
        {
            EnsureArg.IsNotNullOrWhiteSpace(expression, nameof(expression));

            return _cache.GetOrAdd(expression, e =>
            {
                try
                {
                    return _fhirPathProvider.Compile(e);
                }
                catch (Exception ex)
                {
                    throw new SubscriptionCriteriaException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionFhirPathInvalid, e, ex.Message), ex);
                }
            });
        }
    }
}
