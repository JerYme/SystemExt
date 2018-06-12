using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebSockets
{
    public class ClientWebSocketWrapper : IClientWebSocket
    {
        private readonly ClientWebSocket _clientWebSocket;

        public ClientWebSocketWrapper()
        {
            _clientWebSocket = new ClientWebSocket();
        }

        public void SetRequestHeader(string headerName, string headerValue)
            => _clientWebSocket.Options.SetRequestHeader(headerName, headerValue);

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
            => _clientWebSocket.ConnectAsync(uri, cancellationToken);

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
            => _clientWebSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);

        public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => _clientWebSocket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => _clientWebSocket.ReceiveAsync(buffer, cancellationToken);

        public void Dispose() => _clientWebSocket.Dispose();
    }
}