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
    /// A local HTTP endpoint that records notification POSTs. Paths starting with <c>/fail</c> return HTTP 500, and paths starting
    /// with <c>/fail-events</c> accept the handshake but return HTTP 500 for everything else; failed POSTs are recorded in
    /// <see cref="Failed"/>.
    /// </summary>
    public sealed class WebhookReceiver : IDisposable
    {
        private const int MaxBindAttempts = 5;

        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private Exception _lastRequestError;

        public WebhookReceiver()
        {
            // A free port can be taken by another process between the probe and the bind, so try a few.
            for (int attempt = 1; ; attempt++)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                int port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    BaseUrl = $"http://127.0.0.1:{port}";
                    break;
                }
                catch (HttpListenerException) when (attempt < MaxBindAttempts)
                {
                    listener.Close();
                }
            }

            _loop = Task.Run(ListenAsync);
        }

        public string BaseUrl { get; }

        public ConcurrentQueue<ReceivedNotification> Received { get; } = new();

        /// <summary>
        /// Gets the notifications posted to <c>/fail</c> paths, which were answered with HTTP 500.
        /// </summary>
        public ConcurrentQueue<ReceivedNotification> Failed { get; } = new();

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

            string loopState = _loop.IsFaulted ? $" The receiver stopped: {_loop.Exception?.GetBaseException().Message}." : string.Empty;
            string requestError = _lastRequestError == null ? string.Empty : $" Last request error: {_lastRequestError.Message}.";
            throw new TimeoutException($"No matching notification on {path} within {timeout}.{loopState}{requestError} Received: {string.Join(" | ", For(path).Select(n => n.Body.Length > 300 ? n.Body[..300] : n.Body))}");
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

                try
                {
                    await HandleAsync(context);
                }
                catch (Exception ex) when (ex is IOException or HttpListenerException or InvalidOperationException or ObjectDisposedException)
                {
                    // A request the server abandoned, for example on its delivery timeout, must not stop the receiver.
                    _lastRequestError = ex;
                    context.Response.Abort();
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            string body = await reader.ReadToEndAsync();
            string path = context.Request.Url.AbsolutePath;
            var notification = new ReceivedNotification(path, context.Request.ContentType, context.Request.Headers["Authorization"], body);
            bool fails = path.StartsWith("/fail-events", StringComparison.Ordinal)
                ? !body.Contains("\"handshake\"", StringComparison.Ordinal)
                : path.StartsWith("/fail", StringComparison.Ordinal);
            if (fails)
            {
                Failed.Enqueue(notification);
                context.Response.StatusCode = 500;
            }
            else
            {
                Received.Enqueue(notification);
                context.Response.StatusCode = 200;
            }

            context.Response.Close();
        }
    }
}
