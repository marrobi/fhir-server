// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Subscriptions
{
    /// <summary>
    /// A local HTTP endpoint that records notification POSTs. Paths starting with <c>/fail</c> return HTTP 500.
    /// </summary>
    public sealed class WebhookReceiver : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public WebhookReceiver()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(ListenAsync);
        }

        public string BaseUrl { get; }

        public ConcurrentQueue<ReceivedNotification> Received { get; } = new();

        public IReadOnlyList<ReceivedNotification> For(string path) => Received.Where(n => n.Path == path).ToList();

        public async Task<ReceivedNotification> WaitForAsync(string path, Func<ReceivedNotification, bool> predicate, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ReceivedNotification match = For(path).FirstOrDefault(predicate);
                if (match != null)
                {
                    return match;
                }

                await Task.Delay(250);
            }

            throw new TimeoutException($"No matching notification on {path} within {timeout}. Received: {string.Join(" | ", For(path).Select(n => n.Body.Length > 300 ? n.Body[..300] : n.Body))}");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The listener throws when closed.
            }

            _stop.Dispose();
        }

        private async Task ListenAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                string body = await reader.ReadToEndAsync();
                string path = context.Request.Url.AbsolutePath;
                if (path.StartsWith("/fail", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 500;
                }
                else
                {
                    Received.Enqueue(new ReceivedNotification(path, context.Request.ContentType, context.Request.Headers["Authorization"], body));
                    context.Response.StatusCode = 200;
                }

                context.Response.Close();
            }
        }
    }
}
