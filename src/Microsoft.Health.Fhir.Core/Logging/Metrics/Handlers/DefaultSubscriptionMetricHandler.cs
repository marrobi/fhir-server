// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Microsoft.Health.Fhir.Core.Logging.Metrics.Handlers
{
    public sealed class DefaultSubscriptionMetricHandler : BaseMeterMetricHandler, ISubscriptionMetricHandler
    {
        private readonly Counter<long> _pumpPositions;
        private readonly Histogram<double> _pumpLag;
        private readonly Counter<long> _events;
        private readonly Counter<long> _notifications;
        private readonly Histogram<double> _notificationDuration;
        private readonly Counter<long> _statusChanges;

        public DefaultSubscriptionMetricHandler(IMeterFactory meterFactory)
            : base(meterFactory)
        {
            _pumpPositions = MetricMeter.CreateCounter<long>("Subscriptions.PumpPositions", description: "Transactions or change records read by the subscription event pump.");
            _pumpLag = MetricMeter.CreateHistogram<double>("Subscriptions.PumpLag", unit: "s", description: "Age of the newest change read by the subscription event pump.");
            _events = MetricMeter.CreateCounter<long>("Subscriptions.Events", description: "Subscription events created, by outcome.");
            _notifications = MetricMeter.CreateCounter<long>("Subscriptions.Notifications", description: "Subscription notification attempts, by type and outcome.");
            _notificationDuration = MetricMeter.CreateHistogram<double>("Subscriptions.NotificationDuration", unit: "ms", description: "Duration of subscription notification attempts.");
            _statusChanges = MetricMeter.CreateCounter<long>("Subscriptions.StatusChanges", description: "Subscription status changes made by the server, by new status.");
        }

        public void PumpProgress(int positions, TimeSpan lag)
        {
            _pumpPositions.Add(positions);
            _pumpLag.Record(Math.Max(0, lag.TotalSeconds));
        }

        public void EventsCreated(int matched, int unevaluable)
        {
            if (matched > 0)
            {
                _events.Add(matched, new KeyValuePair<string, object>("outcome", "matched"));
            }

            if (unevaluable > 0)
            {
                _events.Add(unevaluable, new KeyValuePair<string, object>("outcome", "unevaluable"));
            }
        }

        public void NotificationSent(string notificationType, bool succeeded, TimeSpan duration)
        {
            var type = new KeyValuePair<string, object>("type", notificationType);
            var outcome = new KeyValuePair<string, object>("outcome", succeeded ? "success" : "failure");
            _notifications.Add(1, type, outcome);
            _notificationDuration.Record(duration.TotalMilliseconds, type, outcome);
        }

        public void StatusChanged(string status)
        {
            _statusChanges.Add(1, new KeyValuePair<string, object>("status", status));
        }
    }
}
