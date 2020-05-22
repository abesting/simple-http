﻿#region License
// Copyright © 2018 Darko Jurić
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleHttp
{
    /// <summary>
    /// HTTP server listener class.
    /// </summary>
    public static class HttpServer
    {
        public static ConcurrentQueue<Task> RequestTasks { get; } = new ConcurrentQueue<Task>();

        /// <summary>
        /// Creates and starts a new instance of the http(s) server.
        /// </summary>
        /// <param name="port">The http/https URI listening port.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onHttpRequestAsync">Action executed on HTTP request.</param>
        /// <param name="useHttps">True to add 'https://' prefix insteaad of 'http://'.</param>
        /// <param name="maxHttpConnectionCount">Maximum HTTP connection count, after which the incoming requests will wait (sockets are not included).</param>
        /// <returns>Server listening task.</returns>
        public static async Task ListenAsync(int port, CancellationToken token, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync, bool useHttps = false, byte maxHttpConnectionCount = 32)
        {
            if (port < 0 || port > UInt16.MaxValue)
                throw new NotSupportedException($"The provided port value must in the range: [0..{UInt16.MaxValue}");

            var s = useHttps ? "s" : String.Empty;
            await ListenAsync($"http{s}://+:{port}/", token, onHttpRequestAsync, maxHttpConnectionCount);
        }

        /// <summary>
        /// Creates and starts a new instance of the http(s) / websocket server.
        /// </summary>
        /// <param name="httpListenerPrefix">The http/https URI listening prefix.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onHttpRequestAsync">Action executed on HTTP request.</param>
        /// <param name="maxHttpConnectionCount">Maximum HTTP connection count, after which the incoming requests will wait (sockets are not included).</param>
        /// <returns>Server listening task.</returns>
        public static async Task ListenAsync(string httpListenerPrefix, CancellationToken token, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync, byte maxHttpConnectionCount = 32)
        {
            //--------------------- checks args
            if (token == null)
                throw new ArgumentNullException(nameof(token), "The provided token must not be null.");

            if (onHttpRequestAsync == null)
                throw new ArgumentNullException(nameof(onHttpRequestAsync), "The provided HTTP request/response action must not be null.");

            if (maxHttpConnectionCount < 1)
                throw new ArgumentException(nameof(maxHttpConnectionCount), "The value must be greater or equal than 1.");

            var listener = new HttpListener();
            try { listener.Prefixes.Add(httpListenerPrefix); }
            catch (Exception ex) { throw new ArgumentException("The provided prefix is not supported. Prefixes have the format: 'http(s)://+:(port)/'", ex); }


            //--------------------- start listener
            try { listener.Start(); }
            catch (Exception ex) when ((ex as HttpListenerException)?.ErrorCode == 5)
            {
                var msg = GetNamespaceReservationExceptionMessage(httpListenerPrefix);
                throw new UnauthorizedAccessException(msg, ex);
            }

            while (RequestTasks.Count > 0)
                RequestTasks.TryDequeue(out _);

            using (var s = new SemaphoreSlim(maxHttpConnectionCount))
            using (var r = token.Register(() => listener.Close()))
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var ctx = await listener.GetContextAsync();

                        if (ctx.Request.IsWebSocketRequest)
                        {
                            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            ctx.Response.Close();
                        }
                        else
                        {
                            await s.WaitAsync();
                            RequestTasks.Enqueue(Task.Run(() => HandleRequest(ctx, onHttpRequestAsync, s), token));
                            var failedTasks = RequestTasks.Where(t => t.Exception != null);
                            if (failedTasks.Count() > 0)
                            {
                                throw new AggregateException(failedTasks.Select(t => t.Exception));
                            }
                            while (RequestTasks.TryPeek(out Task next))
                            {
                                if (next.IsCompleted)
                                    RequestTasks.TryDequeue(out _);
                                else
                                    break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }

        private static async Task HandleRequest(HttpListenerContext ctx, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync, SemaphoreSlim s)
        {
            try
            {
                await onHttpRequestAsync(ctx.Request, ctx.Response);
            }
            finally
            {
                s.Release();
            }
        }

        static string GetNamespaceReservationExceptionMessage(string httpListenerPrefix)
        {
            string msg = null;
            var m = Regex.Match(httpListenerPrefix, @"(?<protocol>\w+)://localhost:?(?<port>\d*)");

            if (m.Success)
            {
                var protocol = m.Groups["protocol"].Value;
                var port = m.Groups["port"].Value; if (String.IsNullOrEmpty(port)) port = 80.ToString();

                msg = $"The HTTP server can not be started, as the namespace reservation already exists.\n" +
                      $"Please run (elevated): 'netsh http delete urlacl url={protocol}://+:{port}/'.";
            }
            else
            {
                msg = $"The HTTP server can not be started, as the namespace reservation does not exist.\n" +
                      $"Please run (elevated): 'netsh http add urlacl url={httpListenerPrefix} user=\"Everyone\"'.";
            }

            return msg;
        }
    }
}
