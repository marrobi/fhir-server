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
using Microsoft.Health.Fhir.Core.Features.Compartment;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// Evaluates search criteria against a single resource version in memory, using the server's own search parameter
    /// parsing and indexing so that results agree with the database search for the supported subset.
    /// </summary>
    /// <remarks>
    /// Criteria that need another database query (chaining, <c>_has</c>, <c>_include</c>), terminology modifiers,
    /// composite parameters, and result parameters (<c>_count</c>, <c>_sort</c>, ...) are rejected by <see cref="Parse"/>.
    /// </remarks>
    public sealed class InMemorySearchEvaluator
    {
        private const string IdParameter = "_id";
        private const string LastUpdatedParameter = "_lastUpdated";

        private static readonly HashSet<string> UnsupportedParameters = new(StringComparer.Ordinal)
        {
            "_count", "_sort", "_include", "_revinclude", "_summary", "_elements", "_contained", "_containedType", "_total",
            "_text", "_content", "_filter", "_query", "_list", "_has", "_type", "_format", "_pretty", "_score", "_notReferenced",
        };

        private static readonly HashSet<string> UnsupportedModifiers = new(StringComparer.Ordinal)
        {
            "above", "below", "in", "not-in", "identifier", "iterate", "ofType", "code-text", "text-advanced",
        };

        private static readonly CompareInfo InvariantCompare = CultureInfo.InvariantCulture.CompareInfo;
        private const CompareOptions IgnoreCaseAndAccents = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        private readonly IExpressionParser _expressionParser;
        private readonly ISearchIndexer _searchIndexer;
        private readonly ICompartmentIndexer _compartmentIndexer;
        private readonly ConcurrentDictionary<(string ResourceType, string Key, string Value), Expression> _cache = new();

        public InMemorySearchEvaluator(IExpressionParser expressionParser, ISearchIndexer searchIndexer, ICompartmentIndexer compartmentIndexer)
        {
            _expressionParser = EnsureArg.IsNotNull(expressionParser, nameof(expressionParser));
            _searchIndexer = EnsureArg.IsNotNull(searchIndexer, nameof(searchIndexer));
            _compartmentIndexer = EnsureArg.IsNotNull(compartmentIndexer, nameof(compartmentIndexer));
        }

        /// <summary>
        /// Parses query parameters for a resource type into expressions, rejecting anything that cannot be evaluated in memory.
        /// </summary>
        /// <param name="resourceType">The resource type the parameters apply to.</param>
        /// <param name="parameters">The decoded query parameters.</param>
        /// <returns>One expression per parameter; a resource matches when all of them match.</returns>
        /// <exception cref="SubscriptionCriteriaException">A parameter is unknown or not supported in memory.</exception>
        public IReadOnlyList<Expression> Parse(string resourceType, IEnumerable<KeyValuePair<string, string>> parameters)
        {
            EnsureArg.IsNotNullOrWhiteSpace(resourceType, nameof(resourceType));
            EnsureArg.IsNotNull(parameters, nameof(parameters));

            var result = new List<Expression>();
            foreach (KeyValuePair<string, string> parameter in parameters)
            {
                result.Add(ParseOne(resourceType, parameter.Key, parameter.Value));
            }

            return result;
        }

        /// <summary>
        /// Returns true when the resource matches every expression.
        /// </summary>
        /// <param name="snapshot">The resource version.</param>
        /// <param name="expressions">Expressions from <see cref="Parse"/>.</param>
        /// <returns>Whether all expressions match.</returns>
        public bool Matches(ResourceSnapshot snapshot, IReadOnlyList<Expression> expressions)
        {
            EnsureArg.IsNotNull(snapshot, nameof(snapshot));
            EnsureArg.IsNotNull(expressions, nameof(expressions));

            if (expressions.Count == 0)
            {
                return true;
            }

            IReadOnlyCollection<SearchIndexEntry> entries = GetSearchIndices(snapshot);
            foreach (Expression expression in expressions)
            {
                if (!EvaluateResourceLevel(expression, snapshot, entries))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns true when the resource belongs to the given compartment.
        /// </summary>
        /// <param name="snapshot">The resource version.</param>
        /// <param name="compartmentType">The compartment type, for example <c>Patient</c>.</param>
        /// <param name="compartmentId">The compartment owner id.</param>
        /// <returns>Whether the resource is in the compartment.</returns>
        public bool IsInCompartment(ResourceSnapshot snapshot, string compartmentType, string compartmentId)
        {
            EnsureArg.IsNotNull(snapshot, nameof(snapshot));

            if (string.Equals(snapshot.ResourceType, compartmentType, StringComparison.Ordinal))
            {
                return string.Equals(snapshot.ResourceId, compartmentId, StringComparison.Ordinal);
            }

            snapshot.CompartmentIndices ??= _compartmentIndexer.Extract(snapshot.ResourceType, GetSearchIndices(snapshot));
            IReadOnlyCollection<string> ids = compartmentType switch
            {
                KnownCompartmentTypes.Patient => snapshot.CompartmentIndices.PatientCompartmentEntry,
                KnownCompartmentTypes.Encounter => snapshot.CompartmentIndices.EncounterCompartmentEntry,
                KnownCompartmentTypes.Practitioner => snapshot.CompartmentIndices.PractitionerCompartmentEntry,
                KnownCompartmentTypes.RelatedPerson => snapshot.CompartmentIndices.RelatedPersonCompartmentEntry,
                KnownCompartmentTypes.Device => snapshot.CompartmentIndices.DeviceCompartmentEntry,
                _ => null,
            };

            return ids != null && ids.Contains(compartmentId, StringComparer.Ordinal);
        }

        private Expression ParseOne(string resourceType, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                throw new SubscriptionCriteriaException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionCriteriaParameterEmpty, key));
            }

            string code = key;
            int colon = key.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0)
            {
                code = key.Substring(0, colon);
                string modifier = key.Substring(colon + 1);
                if (UnsupportedModifiers.Contains(modifier))
                {
                    throw new SubscriptionCriteriaException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionCriteriaModifierNotSupported, modifier, code));
                }
            }

            if (UnsupportedParameters.Contains(code) || ExpressionParser.ContainsChainOrReverseParameter(key))
            {
                throw new SubscriptionCriteriaException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionCriteriaParameterNotSupported, key));
            }

            return _cache.GetOrAdd((resourceType, key, value), k =>
            {
                Expression expression;
                try
                {
                    expression = _expressionParser.Parse([k.ResourceType], k.Key, k.Value);
                }
                catch (Exception ex) when (ex is not SubscriptionCriteriaException)
                {
                    throw new SubscriptionCriteriaException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionCriteriaParameterInvalid, k.Key, k.Value, ex.Message), ex);
                }

                if (!IsSupported(expression, topLevel: true))
                {
                    throw new SubscriptionCriteriaException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionCriteriaParameterNotSupported, k.Key));
                }

                return expression;
            });
        }

        private static bool IsSupported(Expression expression, bool topLevel)
        {
            switch (expression)
            {
                case SearchParameterExpression spe:
                    return topLevel && spe.Parameter.Type != ValueSets.SearchParamType.Composite && IsSupported(spe.Expression, topLevel: false);
                case MissingSearchParameterExpression:
                    return topLevel;
                case MultiaryExpression multiary:
                    return multiary.Expressions.All(e => IsSupported(e, topLevel));
                case NotExpression not:
                    return IsSupported(not.Expression, topLevel);
                case BinaryExpression binary:
                    return !topLevel && binary.ComponentIndex == null;
                case StringExpression str:
                    return !topLevel && str.ComponentIndex == null;
                case MissingFieldExpression missing:
                    return !topLevel && missing.ComponentIndex == null;
                default:
                    return false;
            }
        }

        private IReadOnlyCollection<SearchIndexEntry> GetSearchIndices(ResourceSnapshot snapshot)
        {
            return snapshot.SearchIndices ??= _searchIndexer.Extract(snapshot.Resource);
        }

        private static bool EvaluateResourceLevel(Expression expression, ResourceSnapshot snapshot, IReadOnlyCollection<SearchIndexEntry> entries)
        {
            switch (expression)
            {
                case MultiaryExpression multiary:
                    return multiary.MultiaryOperation == MultiaryOperator.And
                        ? multiary.Expressions.All(e => EvaluateResourceLevel(e, snapshot, entries))
                        : multiary.Expressions.Any(e => EvaluateResourceLevel(e, snapshot, entries));
                case NotExpression not:
                    return !EvaluateResourceLevel(not.Expression, snapshot, entries);
                case MissingSearchParameterExpression missing:
                    return missing.IsMissing == !GetValues(missing.Parameter, snapshot, entries).Any();
                case SearchParameterExpression spe:
                    IEnumerable<ISearchValue> values = GetValues(spe.Parameter, snapshot, entries);

                    // FHIR ':not' (and the multi-value form) means "no value matches", which includes resources without a value.
                    if (spe.Expression is NotExpression notExpression)
                    {
                        return !values.Any(v => EvaluateValue(notExpression.Expression, v));
                    }

                    return values.Any(v => EvaluateValue(spe.Expression, v));
                default:
                    throw new InvalidOperationException($"Unsupported expression '{expression}'.");
            }
        }

        private static List<ISearchValue> GetValues(SearchParameterInfo parameter, ResourceSnapshot snapshot, IReadOnlyCollection<SearchIndexEntry> entries)
        {
            var values = entries.Where(e => e.SearchParameter.Url == parameter.Url).Select(e => e.Value).ToList();
            if (values.Count > 0)
            {
                return values;
            }

            // _id and _lastUpdated are stored as resource columns rather than search indices, so synthesize their values.
            if (string.Equals(parameter.Code, IdParameter, StringComparison.Ordinal))
            {
                return [new TokenSearchValue(null, snapshot.ResourceId, null)];
            }

            if (string.Equals(parameter.Code, LastUpdatedParameter, StringComparison.Ordinal) && snapshot.Resource.LastUpdated.HasValue)
            {
                DateTimeOffset lastUpdated = snapshot.Resource.LastUpdated.Value;
                return [new DateTimeSearchValue(lastUpdated)];
            }

            return values;
        }

        private static bool EvaluateValue(Expression expression, ISearchValue value)
        {
            switch (expression)
            {
                case MultiaryExpression multiary:
                    return multiary.MultiaryOperation == MultiaryOperator.And
                        ? multiary.Expressions.All(e => EvaluateValue(e, value))
                        : multiary.Expressions.Any(e => EvaluateValue(e, value));
                case NotExpression not:
                    return !EvaluateValue(not.Expression, value);
                case MissingFieldExpression missing:
                    return GetField(missing.FieldName, value) is null or "";
                case StringExpression str:
                    return EvaluateString(str, GetField(str.FieldName, value) as string);
                case BinaryExpression binary:
                    return EvaluateBinary(binary, value);
                default:
                    throw new InvalidOperationException($"Unsupported expression '{expression}'.");
            }
        }

        private static object GetField(FieldName fieldName, ISearchValue value)
        {
            return (fieldName, value) switch
            {
                (FieldName.String, StringSearchValue s) => s.String,
                (FieldName.TokenCode, TokenSearchValue t) => t.Code,
                (FieldName.TokenSystem, TokenSearchValue t) => t.System,
                (FieldName.TokenText, TokenSearchValue t) => t.Text,
                (FieldName.QuantityCode, QuantitySearchValue q) => q.Code,
                (FieldName.QuantitySystem, QuantitySearchValue q) => q.System,
                (FieldName.ReferenceBaseUri, ReferenceSearchValue r) => r.BaseUri?.ToString(),
                (FieldName.ReferenceResourceType, ReferenceSearchValue r) => r.ResourceType,
                (FieldName.ReferenceResourceId, ReferenceSearchValue r) => r.ResourceId,
                (FieldName.Uri, UriSearchValue u) => u.Uri,
                (FieldName.DateTimeStart, DateTimeSearchValue d) => d.Start,
                (FieldName.DateTimeEnd, DateTimeSearchValue d) => d.End,
                (FieldName.Number, NumberSearchValue n) => (n.Low, n.High),
                (FieldName.Quantity, QuantitySearchValue q) => (q.Low, q.High),
                _ => null,
            };
        }

        private static bool EvaluateString(StringExpression expression, string field)
        {
            if (field == null)
            {
                return expression.StringOperator is StringOperator.NotContains or StringOperator.NotEndsWith or StringOperator.NotStartsWith;
            }

            string value = expression.Value ?? string.Empty;
            CompareOptions options = expression.IgnoreCase ? IgnoreCaseAndAccents : CompareOptions.Ordinal;

            return expression.StringOperator switch
            {
                StringOperator.Equals => InvariantCompare.Compare(field, value, options) == 0,
                StringOperator.StartsWith => InvariantCompare.IsPrefix(field, value, options),
                StringOperator.EndsWith => InvariantCompare.IsSuffix(field, value, options),
                StringOperator.Contains => InvariantCompare.IndexOf(field, value, options) >= 0,
                StringOperator.NotStartsWith => !InvariantCompare.IsPrefix(field, value, options),
                StringOperator.NotEndsWith => !InvariantCompare.IsSuffix(field, value, options),
                StringOperator.NotContains => InvariantCompare.IndexOf(field, value, options) < 0,
                StringOperator.LeftSideStartsWith => InvariantCompare.IsPrefix(value, field, options),
                _ => false,
            };
        }

        private static bool EvaluateBinary(BinaryExpression expression, ISearchValue value)
        {
            object field = GetField(expression.FieldName, value);
            switch (field)
            {
                case DateTimeOffset date when expression.Value is DateTimeOffset other:
                    return Compare(date.CompareTo(other), expression.BinaryOperator);

                case ValueTuple<decimal?, decimal?> range when expression.Value is decimal number:
                    // Indexed numbers and quantities may be ranges. Compare the bound that can satisfy the operator,
                    // which gives overlap semantics for the range form of "eq".
                    decimal? bound = expression.BinaryOperator switch
                    {
                        BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => range.Item2 ?? range.Item1,
                        BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual => range.Item1 ?? range.Item2,
                        _ => range.Item1 ?? range.Item2,
                    };

                    return bound.HasValue && Compare(bound.Value.CompareTo(number), expression.BinaryOperator);

                default:
                    return false;
            }
        }

        private static bool Compare(int comparison, BinaryOperator binaryOperator)
        {
            return binaryOperator switch
            {
                BinaryOperator.Equal => comparison == 0,
                BinaryOperator.NotEqual => comparison != 0,
                BinaryOperator.GreaterThan => comparison > 0,
                BinaryOperator.GreaterThanOrEqual => comparison >= 0,
                BinaryOperator.LessThan => comparison < 0,
                BinaryOperator.LessThanOrEqual => comparison <= 0,
                _ => false,
            };
        }
    }
}
