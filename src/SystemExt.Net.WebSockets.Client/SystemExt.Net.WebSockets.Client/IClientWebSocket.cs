﻿using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebSockets
{
    /// <summary>
    /// To support usage of Managed WebSocket Client for platform &lt; windows8
    /// </summary>
    public interface IClientWebSocket : IDisposable
    {
        void SetRequestHeader(string headerName, string headerValue);

        Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
        Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken);
        Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
        Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);
    }
}