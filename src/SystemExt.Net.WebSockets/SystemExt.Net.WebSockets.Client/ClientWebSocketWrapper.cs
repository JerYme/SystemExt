using System.Threading;
using System.Threading.Tasks;

#if !NETSTANDARD2_0

#endif

namespace System.Net.WebSockets.Client
{
    /// <summary>
    /// 
    /// </summary>
    public class ClientWebSocketWrapper : IClientWebSocket
    {
        private readonly ClientWebSocket _clientWebSocket;

        /// <summary>
        /// 
        /// </summary>
        public ClientWebSocketWrapper()
        {
            _clientWebSocket = new ClientWebSocket();
        }

        void IClientWebSocket.SetRequestHeader(string headerName, string headerValue)
            => _clientWebSocket.Options.SetRequestHeader(headerName, headerValue);

        Task IClientWebSocket.ConnectAsync(Uri uri, CancellationToken cancellationToken)
            => _clientWebSocket.ConnectAsync(uri, cancellationToken);

        Task IClientWebSocket.CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            => _clientWebSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);

        Task IClientWebSocket.SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => _clientWebSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        Task<WebSocketReceiveResult> IClientWebSocket.ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => _clientWebSocket.ReceiveAsync(buffer, cancellationToken);

        void IDisposable.Dispose() => _clientWebSocket.Dispose();
    }
}