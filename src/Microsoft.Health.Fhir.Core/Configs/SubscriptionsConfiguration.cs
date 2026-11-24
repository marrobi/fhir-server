// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;

namespace Microsoft.Health.Fhir.Core.Configs
{
    /// <summary>
    /// Configuration for FHIR topic-based subscriptions (ADR-2611).
    /// </summary>
    public class SubscriptionsConfiguration
    {
        /// <summary>
        /// Gets or sets a value indicating whether topic-based subscriptions are enabled. Off by default.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets how often, in seconds, the subscription registry is refreshed from stored resources.
        /// </summary>
        public int RegistryRefreshIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// Gets or sets how often, in seconds, the delivery service looks for work.
        /// </summary>
        public double DeliveryPollingIntervalSeconds { get; set; } = 1;

        /// <summary>
        /// Gets or sets the per-subscription delivery lease, in seconds.
        /// </summary>
        public int DeliveryLeaseSeconds { get; set; } = 60;

        /// <summary>
        /// Gets or sets the maximum number of delivery attempts for a notification before the subscription is set to <c>error</c>.
        /// </summary>
        public int MaxDeliveryAttempts { get; set; } = 5;

        /// <summary>
        /// Gets or sets the base delay, in seconds, for exponential back-off between delivery attempts.
        /// </summary>
        public double RetryBaseDelaySeconds { get; set; } = 2;

        /// <summary>
        /// Gets or sets the HTTP timeout, in seconds, used when a subscription does not specify one.
        /// </summary>
        public int DefaultTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets the upper bound, in seconds, for a subscription-requested HTTP timeout.
        /// </summary>
        public int MaxTimeoutSeconds { get; set; } = 120;

        /// <summary>
        /// Gets or sets the minimum heartbeat period, in seconds, a subscription may request.
        /// </summary>
        public int MinHeartbeatPeriodSeconds { get; set; } = 60;

        /// <summary>
        /// Gets or sets the maximum number of events in a single notification.
        /// </summary>
        public int MaxEventsPerNotification { get; set; } = 20;

        /// <summary>
        /// Gets or sets the maximum number of subscriptions that may be active at once.
        /// </summary>
        public int MaxActiveSubscriptions { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the maximum subscription lifetime, in days. A subscription without <c>end</c>, or with a later <c>end</c>,
        /// is given an <c>end</c> at most this far in the future. Zero disables the limit.
        /// </summary>
        public int MaxLifetimeDays { get; set; } = 365;

        /// <summary>
        /// Gets or sets the number of days delivered and unevaluable events are retained.
        /// </summary>
        public int EventRetentionDays { get; set; } = 7;

        /// <summary>
        /// Gets or sets the maximum number of transactions the event pump commits at once.
        /// </summary>
        public int PumpTransactionBatchSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the event pump period, in seconds.
        /// </summary>
        public double PumpPeriodSeconds { get; set; } = 1;

        /// <summary>
        /// Gets or sets how far, in seconds, the <see cref="SubscriptionEventSource.Transactions"/> source stays behind the newest transaction ids.
        /// Transaction ids come from the SQL clock, so a write that starts after the clock steps back gets a lower id than earlier writes;
        /// it is read only if it commits within this lag. Zero reads up to the visibility watermark.
        /// The same margin applies before a subscription's activation, so changes made up to this long before activation can also notify.
        /// </summary>
        public double TransactionReadLagSeconds { get; set; } = 5;

        /// <summary>
        /// Gets or sets how long, in hours, a subscription may stay in <c>error</c> before it is set to <c>off</c> and its pending events
        /// are discarded. Bounds the events kept for an unreachable endpoint. Zero disables the limit.
        /// </summary>
        public int MaxErrorHours { get; set; } = 72;

        /// <summary>
        /// Gets or sets the maximum lag, in hours, by which the event pump may hold back invisible-history cleanup.
        /// </summary>
        public int MaxHistoryCleanupHoldHours { get; set; } = 24;

        /// <summary>
        /// Gets or sets the committed-change source the event pump reads. Defaults to <see cref="SubscriptionEventSource.Transactions"/>.
        /// </summary>
        public SubscriptionEventSource EventSource { get; set; } = SubscriptionEventSource.Transactions;

        /// <summary>
        /// Gets or sets a value indicating whether versions that are written directly as history notify subscribers. Off by default.
        /// <c>$import</c> writes earlier versions this way when it loads a resource's history; they describe past changes.
        /// </summary>
        public bool NotifyHistoricalVersions { get; set; }

        /// <summary>
        /// Gets or sets how topics on resource types with the <c>no-version</c> versioning policy are treated.
        /// </summary>
        public UnversionedResourceTypePolicy UnversionedResourceTypes { get; set; } = UnversionedResourceTypePolicy.AllowWithGaps;

        /// <summary>
        /// Gets or sets a value indicating whether plain HTTP endpoints are allowed. Intended for local development only.
        /// </summary>
        public bool AllowHttpEndpoints { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether endpoints on loopback, private, or link-local addresses are allowed.
        /// Intended for local development only.
        /// </summary>
        public bool AllowPrivateNetworkEndpoints { get; set; }

        /// <summary>
        /// Gets the allow-list of endpoint host names. When non-empty, only these hosts (or their subdomains when the
        /// entry starts with <c>*.</c>) may receive notifications.
        /// </summary>
        public IList<string> AllowedEndpointHosts { get; } = new List<string>();
    }
}
