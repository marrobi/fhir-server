// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Api.Modules;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Api.UnitTests.Modules
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionsModuleTests
    {
        [Fact]
        public void GivenTheRestHookHandler_WhenCreated_ThenItNeitherUsesAProxyNorFollowsRedirects()
        {
            // A proxy would receive the connection, so the address check would apply to the proxy instead of the endpoint.
            using var handler = SubscriptionsModule.CreateRestHookHandler(Policy(allowPrivate: false));

            Assert.False(handler.UseProxy);
            Assert.False(handler.AllowAutoRedirect);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public async Task GivenALoopbackEndpoint_WhenConnecting_ThenThePolicyDecidesWhetherTheConnectionIsMade(bool allowPrivate, bool connects)
        {
            // Arrange
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var client = new HttpClient(SubscriptionsModule.CreateRestHookHandler(Policy(allowPrivate)));
                Task<TcpClient> accepted = listener.AcceptTcpClientAsync();

                // Act
                Task<HttpResponseMessage> send = client.PostAsync($"http://localhost:{port}/hook", new StringContent("{}"));
                Task first = await Task.WhenAny(send, accepted);

                // Assert
                if (connects)
                {
                    Assert.Same(accepted, first);
                    (await accepted).Dispose();
                }
                else
                {
                    HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() => send);
                    Assert.Contains("localhost", ex.Message, System.StringComparison.Ordinal);
                    Assert.False(accepted.IsCompleted);
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static SubscriptionEndpointPolicy Policy(bool allowPrivate)
        {
            var configuration = new SubscriptionsConfiguration { AllowHttpEndpoints = true };
            if (allowPrivate)
            {
                configuration.AllowedPrivateNetworks.Add("127.0.0.0/8");
                configuration.AllowedPrivateNetworks.Add("::1/128");
            }

            return new(Options.Create(configuration));
        }
    }
}
