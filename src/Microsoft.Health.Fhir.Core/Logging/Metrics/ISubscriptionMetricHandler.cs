// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Logging.Metrics
{
    /// <summary>
    /// Emits metrics for topic-based subscriptions (ADR-2611).
    /// </summary>
    public interface ISubscriptionMetricHandler
    {
        /// <summary>
        /// Records that the event pump processed a page of the event source.
        /// </summary>
        /// <param name="positions">The number of transactions or change records read.</param>
        /// <param name="lag">How far behind the newest position read is from now.</param>
        void PumpProgress(int positions, TimeSpan lag);

        /// <summary>
        /// Records the events the pump created.
        /// </summary>
        /// <param name="matched">The number of events that will be delivered.</param>
        /// <param name="unevaluable">The number of events that could not be evaluated and will not be delivered.</param>
        void EventsCreated(int matched, int unevaluable);

        /// <summary>
        /// Records one attempt to send a notification.
        /// </summary>
        /// <param name="notificationType">The notification type, for example <c>handshake</c> or <c>event-notification</c>.</param>
        /// <param name="succeeded">Whether the endpoint accepted it.</param>
        /// <param name="duration">How long the attempt took.</param>
        void NotificationSent(string notificationType, bool succeeded, TimeSpan duration);

        /// <summary>
        /// Records a status change made by the server.
        /// </summary>
        /// <param name="status">The new status code.</param>
        void StatusChanged(string status);
    }
}
