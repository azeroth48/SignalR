﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR.Client.Http;
using Microsoft.AspNet.SignalR.Hosting.Common;
using IClientRequest = Microsoft.AspNet.SignalR.Client.Http.IRequest;
using IClientResponse = Microsoft.AspNet.SignalR.Client.Http.IResponse;

namespace Microsoft.AspNet.SignalR.Hosting.Memory
{
    public class MemoryHost : RoutingHost, IHttpClient, IDisposable
    {
        private readonly CancellationTokenSource _shutDownToken = new CancellationTokenSource();
        private int _disposed;

        public MemoryHost()
            : this(new DefaultDependencyResolver())
        {

        }

        public MemoryHost(IDependencyResolver resolver)
            : base(resolver)
        {
            resolver.InitializePerformanceCounters(Process.GetCurrentProcess().GetUniqueInstanceName(_shutDownToken.Token), _shutDownToken.Token);

            User = Thread.CurrentPrincipal;
        }

        public string InstanceName { get; set; }

        public IPrincipal User { get; set; }

        Task<IClientResponse> IHttpClient.GetAsync(string url, Action<IClientRequest> prepareRequest)
        {
            return ProcessRequest(url, prepareRequest, postData: null);
        }

        Task<IClientResponse> IHttpClient.PostAsync(string url, Action<IClientRequest> prepareRequest, Dictionary<string, string> postData)
        {
            return ProcessRequest(url, prepareRequest, postData);
        }

        public Task<IClientResponse> ProcessRequest(string url, Action<IClientRequest> prepareRequest, Dictionary<string, string> postData, bool disableWrites = false)
        {
            var uri = new Uri(url);
            PersistentConnection connection;

            if (!_shutDownToken.IsCancellationRequested && TryGetConnection(uri.LocalPath, out connection))
            {
                var tcs = new TaskCompletionSource<IClientResponse>();
                var clientTokenSource = new CancellationTokenSource();
                var request = new Request(uri, clientTokenSource, postData, User);
                prepareRequest(request);

                Response response = null;
                response = new Response(clientTokenSource.Token, () => tcs.TrySetResult(response))
                {
                    DisableWrites = disableWrites
                };
                var hostContext = new HostContext(request, response);

                hostContext.Items[HostConstants.ShutdownToken] = _shutDownToken.Token;

                connection.Initialize(DependencyResolver, hostContext);

                connection.ProcessRequestAsync(hostContext).ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        tcs.TrySetException(task.Exception);
                    }
                    else if (task.IsCanceled)
                    {
                        tcs.TrySetCanceled();
                    }
                    else
                    {
                        tcs.TrySetResult(response);
                    }

                    response.Close();
                });

                return tcs.Task;
            }

            return TaskAsyncHelper.FromError<IClientResponse>(new InvalidOperationException("Not a valid end point"));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _shutDownToken.Cancel(throwOnFirstException: false);
                DependencyResolver.Dispose();
            }
        }
    }
}
