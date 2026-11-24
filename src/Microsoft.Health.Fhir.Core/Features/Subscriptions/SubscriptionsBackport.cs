// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Canonical URLs used by the Subscriptions R5 Backport IG and by this server's R4 topic profile.
    /// </summary>
    public static class SubscriptionsBackport
    {
        public const string Base = "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/";

        public const string SubscriptionProfile = Base + "backport-subscription";

        public const string StatusR4Profile = Base + "backport-subscription-status-r4";

        public const string FilterCriteria = Base + "backport-filter-criteria";

        public const string HeartbeatPeriod = Base + "backport-heartbeat-period";

        public const string Timeout = Base + "backport-timeout";

        public const string MaxCount = Base + "backport-max-count";

        public const string ChannelType = Base + "backport-channel-type";

        public const string PayloadContent = Base + "backport-payload-content";

        public const string TopicCanonicalCapability = Base + "capabilitystatement-subscriptiontopic-canonical";

        public const string ServerCapabilityStatementR4 = "http://hl7.org/fhir/uv/subscriptions-backport/CapabilityStatement/backport-subscription-server-r4";

        public const string ServerCapabilityStatementR4B = "http://hl7.org/fhir/uv/subscriptions-backport/CapabilityStatement/backport-subscription-server";

        public const string ChannelTypeSystem = "http://terminology.hl7.org/CodeSystem/subscription-channel-type";

        /// <summary>
        /// The code system of <c>Basic.code</c> for R4 topics.
        /// </summary>
        public const string FhirTypesSystem = "http://hl7.org/fhir/fhir-types";

        /// <summary>
        /// The R5 cross-version extension prefix used to carry <c>SubscriptionTopic</c> elements on an R4 <c>Basic</c> resource.
        /// </summary>
        public const string TopicExtensionPrefix = "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.";

        /// <summary>
        /// This server's profile for R4 topics stored as <c>Basic</c> (ADR-2611). It is server-specific: the Backport IG leaves R4
        /// topic definitions out of scope.
        /// </summary>
        public const string R4TopicProfile = "http://fhir.microsoft.com/StructureDefinition/subscription-topic-basic";
    }
}
