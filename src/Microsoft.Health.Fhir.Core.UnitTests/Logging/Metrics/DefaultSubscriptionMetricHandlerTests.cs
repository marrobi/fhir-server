// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Microsoft.Health.Fhir.Core.Logging.Metrics.Handlers;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Logging.Metrics
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public sealed class DefaultSubscriptionMetricHandlerTests : IDisposable
    {
        private readonly TestMeterFactory _meterFactory = new();
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, double Value, Dictionary<string, object> Tags)> _measurements = [];

        public DefaultSubscriptionMetricHandlerTests()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (_meterFactory.Meters.Contains(instrument.Meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _listener.Start();
        }

        [Fact]
        public void GivenSubscriptionActivity_WhenRecorded_ThenEachInstrumentReportsItWithItsTags()
        {
            // Arrange
            var handler = new DefaultSubscriptionMetricHandler(_meterFactory);

            // Act
            handler.PumpProgress(7, TimeSpan.FromSeconds(3));
            handler.EventsCreated(matched: 2, unevaluable: 1);
            handler.NotificationSent("EventNotification", succeeded: false, TimeSpan.FromMilliseconds(40));
            handler.StatusChanged("Error");

            // Assert
            Assert.Equal(7, Single("Subscriptions.PumpPositions").Value);
            Assert.Equal(3, Single("Subscriptions.PumpLag").Value);
            Assert.Equal(2, Single("Subscriptions.Events", "outcome", "matched").Value);
            Assert.Equal(1, Single("Subscriptions.Events", "outcome", "unevaluable").Value);
            (string _, double _, Dictionary<string, object> notificationTags) = Single("Subscriptions.Notifications");
            Assert.Equal("EventNotification", notificationTags["type"]);
            Assert.Equal("failure", notificationTags["outcome"]);
            Assert.Equal(40, Single("Subscriptions.NotificationDuration").Value);
            Assert.Equal("Error", Single("Subscriptions.StatusChanges").Tags["status"]);
        }

        [Fact]
        public void GivenNoEventsOfAnOutcome_WhenRecorded_ThenNoZeroMeasurementIsEmitted()
        {
            var handler = new DefaultSubscriptionMetricHandler(_meterFactory);

            handler.EventsCreated(matched: 0, unevaluable: 0);

            Assert.DoesNotContain(_measurements, m => m.Instrument == "Subscriptions.Events");
        }

        public void Dispose()
        {
            _listener.Dispose();
            _meterFactory.Dispose();
        }

        private (string Instrument, double Value, Dictionary<string, object> Tags) Single(string instrument, string tag = null, object tagValue = null)
        {
            return Assert.Single(_measurements, m => m.Instrument == instrument && (tag == null || Equals(m.Tags[tag], tagValue)));
        }

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            _measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        }

        private sealed class TestMeterFactory : IMeterFactory
        {
            public List<Meter> Meters { get; } = [];

            public Meter Create(MeterOptions options)
            {
                var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
                Meters.Add(meter);
                return meter;
            }

            public void Dispose()
            {
                foreach (Meter meter in Meters)
                {
                    meter.Dispose();
                }
            }
        }
    }
}
