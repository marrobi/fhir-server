// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Models
{
    /// <summary>
    /// FHIR code literals and lifecycle groupings of <see cref="SubscriptionStatusCode"/> and <see cref="SubscriptionNotificationType"/>.
    /// </summary>
    public static class SubscriptionStatusCodeExtensions
    {
        /// <summary>
        /// Parses a <c>subscription-status</c> code. A missing or unknown code is <see cref="SubscriptionStatusCode.Requested"/>.
        /// </summary>
        /// <param name="literal">The code.</param>
        /// <returns>The status.</returns>
        public static SubscriptionStatusCode ParseSubscriptionStatus(string literal) => literal switch
        {
            "active" => SubscriptionStatusCode.Active,
            "error" => SubscriptionStatusCode.Error,
            "off" => SubscriptionStatusCode.Off,
            "entered-in-error" => SubscriptionStatusCode.EnteredInError,
            _ => SubscriptionStatusCode.Requested,
        };

        /// <summary>
        /// Gets the <c>subscription-status</c> code.
        /// </summary>
        /// <param name="status">The status.</param>
        /// <returns>The code.</returns>
        public static string ToLiteral(this SubscriptionStatusCode status) => status switch
        {
            SubscriptionStatusCode.Active => "active",
            SubscriptionStatusCode.Error => "error",
            SubscriptionStatusCode.Off => "off",
            SubscriptionStatusCode.EnteredInError => "entered-in-error",
            _ => "requested",
        };

        /// <summary>
        /// Gets the <c>subscription-notification-type</c> code.
        /// </summary>
        /// <param name="type">The notification type.</param>
        /// <returns>The code.</returns>
        public static string ToLiteral(this SubscriptionNotificationType type) => type switch
        {
            SubscriptionNotificationType.Handshake => "handshake",
            SubscriptionNotificationType.Heartbeat => "heartbeat",
            SubscriptionNotificationType.QueryStatus => "query-status",
            _ => "event-notification",
        };

        /// <summary>
        /// Gets a value indicating whether the subscription is live: it counts toward the limit, holds a grant and is matched.
        /// </summary>
        /// <param name="status">The status.</param>
        /// <returns>Whether the status is requested, active or error.</returns>
        public static bool IsLive(this SubscriptionStatusCode status) =>
            status is SubscriptionStatusCode.Requested or SubscriptionStatusCode.Active or SubscriptionStatusCode.Error;

        /// <summary>
        /// Gets a value indicating whether the subscription is in error or stopped, so the server must not set it to error again.
        /// </summary>
        /// <param name="status">The status.</param>
        /// <returns>Whether the status is error, off or entered-in-error.</returns>
        public static bool IsErrorOrStopped(this SubscriptionStatusCode status) =>
            status is SubscriptionStatusCode.Error or SubscriptionStatusCode.Off or SubscriptionStatusCode.EnteredInError;
    }
}
