// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// Parses the query strings used by topic criteria and Backport filter criteria,
    /// for example <c>status=in-progress</c> or <c>Encounter?patient=Patient/1&amp;status=finished</c>.
    /// </summary>
    public static class SubscriptionQueryParser
    {
        /// <summary>
        /// Parses a query string into decoded key/value pairs.
        /// </summary>
        /// <param name="query">The query, optionally prefixed with <c>ResourceType?</c> or <c>?</c>.</param>
        /// <param name="resourceType">The resource type prefix, if any.</param>
        /// <returns>The decoded parameters in order.</returns>
        public static IReadOnlyList<KeyValuePair<string, string>> Parse(string query, out string resourceType)
        {
            resourceType = null;
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            string remaining = query.Trim();
            int questionMark = remaining.IndexOf('?', StringComparison.Ordinal);
            if (questionMark >= 0)
            {
                string prefix = remaining.Substring(0, questionMark).Trim();
                resourceType = prefix.Length == 0 ? null : prefix;
                remaining = remaining.Substring(questionMark + 1);
            }
            else if (!remaining.Contains('=', StringComparison.Ordinal))
            {
                // A bare resource type, such as "Encounter", has no parameters.
                resourceType = remaining;
                return result;
            }

            foreach (string part in remaining.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0)
                {
                    throw new FormatException(string.Format(Core.Resources.SubscriptionInvalidCriteriaParameter, part));
                }

                string key = Uri.UnescapeDataString(part.AsSpan(0, equals));
                string value = Uri.UnescapeDataString(part.AsSpan(equals + 1));
                result.Add(new KeyValuePair<string, string>(key, value));
            }

            return result;
        }
    }
}
