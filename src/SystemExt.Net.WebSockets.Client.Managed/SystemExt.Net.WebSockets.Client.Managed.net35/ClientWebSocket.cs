// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using WebSocketHandle = System.Net.WebSockets.Managed.ManagedWebSocketHandle;

namespace System.Net.WebSockets.Managed
{
    public sealed partial class ClientWebSocket : IDisposable
    {
        private enum InternalState
        {
            Created = 0,
            Connecting = 1,
            Connected = 2,
            Disposed = 3
        }

        private readonly ClientWebSocketOptions _options;
        private WebSocketHandle _innerWebSocket; // may be mutable struct; do not make readonly

        // NOTE: this is really an InternalState value, but Interlocked doesn't support
        //       operations on values of enum types.
        private int _state;

        public ClientWebSocket()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
            WebSocketHandle.CheckPlatformSupport();

            _state = (int)InternalState.Created;
            _options = new ClientWebSocketOptions();

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        #region Properties

        public ClientWebSocketOptions Options
        {
            get
            {
                return _options;
            }
        }

        public WebSocketCloseStatus? CloseStatus => WebSocketHandle.IsValid(_innerWebSocket) ? _innerWebSocket.CloseStatus : null;

        public string CloseStatusDescription => WebSocketHandle.IsValid(_innerWebSocket) ? _innerWebSocket.CloseStatusDescription : null;

        public string SubProtocol => WebSocketHandle.IsValid(_innerWebSocket) ? _innerWebSocket.SubProtocol : null;

        public WebSocketState State
        {
            get
            {
                // state == Connected or Disposed
                if (WebSocketHandle.IsValid(_innerWebSocket))
                {
                    return _innerWebSocket.State;
                }
                switch ((InternalState)_state)
                {
                    case InternalState.Created:
                        return WebSocketState.None;
                    case InternalState.Connecting:
                        return WebSocketState.Connecting;
                    default: // We only get here if disposed before connecting
                        Debug.Assert((InternalState)_state == InternalState.Disposed);
                        return WebSocketState.Closed;
                }
            }
        }

        #endregion Properties

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (uri == null)
            {
                throw new ArgumentNullException(nameof(uri));
            }
            if (!uri.IsAbsoluteUri)
            {
                throw new ArgumentException(SR.net_uri_NotAbsolute, nameof(uri));
            }
            if (uri.Scheme != UriScheme.Ws && uri.Scheme != UriScheme.Wss)
            {
                throw new ArgumentException(SR.net_WebSockets_Scheme, nameof(uri));
            }

            // Check that we have not started already
            var priorState = (InternalState)Interlocked.CompareExchange(ref _state, (int)InternalState.Connecting, (int)InternalState.Created);
            if (priorState == InternalState.Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            if (priorState != InternalState.Created)
            {
                throw new InvalidOperationException(SR.net_WebSockets_AlreadyStarted);
            }
            _options.SetToReadOnly();

            return ConnectAsyncCore(uri, cancellationToken);
        }

        private Task ConnectAsyncCore(Uri uri, CancellationToken cancellationToken)
        {
            _innerWebSocket = WebSocketHandle.Create();

            try
            {
                // Change internal state to 'connected' to enable the other methods
                if ((InternalState)Interlocked.CompareExchange(ref _state, (int)InternalState.Connected, (int)InternalState.Connecting) != InternalState.Connecting)
                {
                    // Aborted/Disposed during connect.
                    throw new ObjectDisposedException(GetType().FullName);
                }

                return _innerWebSocket.ConnectAsyncCore(uri, cancellationToken, _options);
            }
            catch (Exception ex)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Error(this, ex);
                throw;
            }
        }

        public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            ThrowIfNotConnected();
            return _innerWebSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ThrowIfNotConnected();
            return _innerWebSocket.ReceiveAsync(buffer, cancellationToken);
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            ThrowIfNotConnected();
            return _innerWebSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            ThrowIfNotConnected();
            return _innerWebSocket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        }

        public void Abort()
        {
            if ((InternalState)_state == InternalState.Disposed)
            {
                return;
            }
            if (WebSocketHandle.IsValid(_innerWebSocket))
            {
                _innerWebSocket.Abort();
            }
            Dispose();
        }

        public void Dispose()
        {
            var priorState = (InternalState)Interlocked.Exchange(ref _state, (int)InternalState.Disposed);
            if (priorState == InternalState.Disposed)
            {
                // No cleanup required.
                return;
            }
            if (WebSocketHandle.IsValid(_innerWebSocket))
            {
                _innerWebSocket.Dispose();
            }
        }

        private void ThrowIfNotConnected()
        {
            if ((InternalState)_state == InternalState.Disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            else if ((InternalState)_state != InternalState.Connected)
            {
                throw new InvalidOperationException(SR.net_WebSockets_NotConnected);
            }
        }
    }
}