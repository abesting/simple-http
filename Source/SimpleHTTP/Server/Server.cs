#region License
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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleHttp
{
    /// <summary>
    /// HTTP server listener class.
    /// </summary>
    public static class HttpServer
    {
        private static string host = "+";

        /// <summary>
        /// Creates and starts a new instance of the http(s) server binding to all local interfaces.
        /// </summary>
        /// <param name="port">The http/https URI listening port.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onHttpRequestAsync">Action executed on HTTP request.</param>
        /// <param name="postStart">Function to execute once http server is listening.</param>
        /// <param name="localEndpointFilter">Enumerable of local endpoint URIs for filtering incoming requests.</param>
        /// <param name="useHttps">True to add 'https://' prefix instead of 'http://'.</param>
        /// <param name="maxHttpConnectionCount">Maximum HTTP connection count, after which the incoming requests will wait (sockets are not included).</param>
        /// <returns>Server listening task.</returns>
        public static async Task ListenAsync(int port, CancellationToken token, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync, Func<Task> postStart = null, IEnumerable<string> localEndpointFilter = null, bool useHttps = false, bool useIpV6 = false, byte maxHttpConnectionCount = 32)
        {
            if (port < 0 || port > UInt16.MaxValue)
                throw new ArgumentException($"The provided port value must in the range: [0..{UInt16.MaxValue}");

            var s = useHttps ? "s" : String.Empty;
            host = useIpV6 ? "*" : "+";
            await ListenAsync(new [] {$"http{s}://{host}:{port}/"}, token, onHttpRequestAsync, postStart, localEndpointFilter, maxHttpConnectionCount).ConfigureAwait(false);                
        }        

        /// <summary>
        /// Creates and starts a new instance of the http(s) server.
        /// </summary>
        /// <param name="httpListenerPrefixes">The http/https URI listening prefixes.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onHttpRequestAsync">Action executed on HTTP request.</param>
        /// <param name="postStart">Function to execute once http server is listening.</param>
        /// <param name="localEndpointFilter">Enumerable of local endpoint URIs for filtering incoming requests.</param>
        /// <param name="maxHttpConnectionCount">Maximum HTTP connection count, after which the incoming requests will wait (sockets are not included).</param>
        /// <returns>Server listening task.</returns>
        public static async Task ListenAsync(IEnumerable<string> httpListenerPrefixes, CancellationToken token, Func<HttpListenerRequest, HttpListenerResponse, Task> onHttpRequestAsync, Func<Task> postStart = null, IEnumerable<string> localEndpointFilter = null, byte maxHttpConnectionCount = 32)
        {
            //--------------------- checks args
            if (token == null)
                throw new ArgumentNullException(nameof(token), "The provided token must not be null.");

            if (onHttpRequestAsync == null)
                throw new ArgumentNullException(nameof(onHttpRequestAsync), "The provided HTTP request/response action must not be null.");

            if (maxHttpConnectionCount < 1)
                throw new ArgumentException(nameof(maxHttpConnectionCount), "The value must be greater or equal than 1.");

            var listener = new HttpListener();

            foreach (var prefix in httpListenerPrefixes)
            {
                string next = prefix;
                if (!next.EndsWith("/"))
                    next += "/";
                try
                {
                    listener.Prefixes.Add(next);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        $"The provided prefix is not supported. Prefixes have the format: 'http(s)://{host}:(port)/'", ex);
                }
            }
            
            try
            {
                listener.Start();
            }
            catch (Exception ex) when ((ex as HttpListenerException)?.ErrorCode == 5)
            {
                string msg = $"The HTTP server can not be started, as the namespace reservation probably does not exist.\n" +
                             $"Windows users, please run as admin: 'netsh http add urlacl url=http(s)://{host}:(port)/ user=Everyone'.";
                throw new UnauthorizedAccessException(msg, ex);
            }

            if (postStart != null)
                await postStart();
            
            using (var semaphore = new SemaphoreSlim(maxHttpConnectionCount))
            using (var closer = token.Register(() => listener.Close()))
            {
                async void SafeHandleRequestAsync(HttpListenerContext ctx)
                {
                    try
                    {
                        await onHttpRequestAsync(ctx.Request, ctx.Response).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        ctx.Response.StatusCode = (int) HttpStatusCode.InternalServerError;
                        ctx.Response.AsText(e.Message);
                        ctx.Response.Close();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var ctx = await listener.GetContextAsync().ConfigureAwait(false);

                        if (ctx.Request.IsWebSocketRequest)
                        {
                            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            ctx.Response.Close();
                            continue;
                        }
                        
                        if (localEndpointFilter != null && !localEndpointFilter.Any(ep => ep.Contains(ctx.Request.LocalEndPoint.ToString())))
                        {
                            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            ctx.Response.Close();
                            continue;
                        }                        

                        await semaphore.WaitAsync(token).ConfigureAwait(false);
                        SafeHandleRequestAsync(ctx);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }
            }
        }
        
    }
}
