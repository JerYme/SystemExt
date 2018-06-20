using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.WebSockets.Client
{
    /// <summary>Wrapper around ClientWebSocket that provides suitable interface to exchange text messages over Web Sockets in event based way.</summary>
    /// <seealso cref="System.Net.WebSockets.ClientWebSocket"/>
    public sealed class ClientWebSocketText : IDisposable
    {
        private readonly IClientWebSocket _clientWebSocket;
        private readonly int _bufferSize;
        private readonly CancellationToken _cancellationToken;
        private readonly Lazy<BufferingContext> _lazyReceiveContext;
        private event EventHandler<MessageReceivedEventArgs> _messageReceived;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="bufferSize"></param>
        /// <param name="cancellationToken"></param>
        public ClientWebSocketText(int bufferSize = 1024, CancellationToken cancellationToken = default(CancellationToken))
            : this(new ClientWebSocketWrapper(), bufferSize, cancellationToken)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientWebSocket"></param>
        /// <param name="bufferSize"></param>
        /// <param name="cancellationToken"></param>
        public ClientWebSocketText(IClientWebSocket clientWebSocket, int bufferSize = 1024, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (clientWebSocket == null) throw new ArgumentNullException(nameof(clientWebSocket));
            if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize), "Receive buffer size should be greater than zero");

            _clientWebSocket = clientWebSocket;
            _bufferSize = bufferSize;
            _cancellationToken = cancellationToken;
            _lazyReceiveContext = new Lazy<BufferingContext>(() => new BufferingContextShared(bufferSize), LazyThreadSafetyMode.None);
        }



        /// <summary>Signals that response message fully received and ready to process.</summary>
        public event EventHandler<MessageReceivedEventArgs> MessageReceived
        {
            add
            {
                var handler = _messageReceived;
                _messageReceived = handler + value;

                if ((handler?.GetInvocationList().Length).GetValueOrDefault() == 0)
                {
                    Task.Factory.StartNew(async () =>
                    {
                        await ReceiveLoopAsync(new BufferingContext(_bufferSize), _cancellationToken, -1);
                    }, _cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Current);
                }
            }
            remove { _messageReceived -= value; }
        }

        /// <summary>Signals that the websocket received an error.</summary>
        public event EventHandler<SocketErrorEventArgs> ErrorReceived;

        /// <summary>Signals that the websocket was closed.</summary>
        public event EventHandler Closed;

        /// <summary>Signals that the socket has opened a connection.</summary>
        public event EventHandler Opened;

        /// <summary>Asynchronously connects to WebSocket server and start receiving income messages in separate Task.</summary>
        /// <param name="url">The <see cref="Uri"/> of the WebSocket server to connect to.</param>
        /// <param name="cancellationToken"></param>
        public async Task ConnectAsync(Uri url, CancellationToken cancellationToken = default(CancellationToken))
        {
            await _clientWebSocket.ConnectAsync(url, cancellationToken);
            Opened?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Disconnects the WebSocket gracefully from the server.</summary>
        public async Task CloseAsync(WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure, string statusDescription = "Client closed the connection", CancellationToken cancellationToken = default(CancellationToken))
        {
            await _clientWebSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);
            Closed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Adds custom request headers to the initial request.</summary>
        /// <param name="headers">A list of custom request headers.</param>
        public void AddHeaders(params KeyValuePair<string, string>[] headers)
        {
            foreach (var header in headers)
                _clientWebSocket.SetRequestHeader(header.Key, header.Value);
        }

        /// <summary>Asynchronously sends message to WebSocket server</summary>
        /// <param name="str">Message to send</param>
        /// <param name="cancellationToken"></param>
        public Task SendAsync(string str, CancellationToken cancellationToken = default(CancellationToken))
        {
            var bytes = Encoding.UTF8.GetBytes(str);
            return _clientWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<string> ReceiveAsync(CancellationToken cancellationToken = default(CancellationToken)) => await ReceiveLoopAsync(_lazyReceiveContext.Value, cancellationToken, 1);

        private async Task<string> ReceiveLoopAsync(BufferingContext bufferingContext, CancellationToken cancellationToken, int loop)
        {
            using (bufferingContext)
            {
                var encoding = bufferingContext.Encoding;
                var bufferBytes = bufferingContext.BufferBytes;
                var bufferChars = bufferingContext.BufferChars;
                var sb = bufferingContext.StringBuilder;

                try
                {
                    string response = null;
                    while ((loop == -1 || --loop >= 0) && !cancellationToken.IsCancellationRequested)
                    {
                        response = await ReceiveMessageAsync(cancellationToken, bufferBytes, bufferChars, encoding, sb);
                        sb = sb.Clear();
                    }

                    if (cancellationToken.IsCancellationRequested) return await TaskEx.FromCanceled<string>(cancellationToken);
                    return response;
                }
                catch (AggregateException ex)
                {
                    ErrorReceived?.Invoke(this, new SocketErrorEventArgs { Exception = ex.Flatten() });
                }
                catch (Exception ex)
                {
                    ErrorReceived?.Invoke(this, new SocketErrorEventArgs { Exception = ex });
                }
            }
            return await Task.FromResult(string.Empty);
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken, byte[] bufferBytes, char[] bufferChars, Encoding encoding, StringBuilder sb)
        {
            WebSocketReceiveResult result;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var writeSegment = new ArraySegment<byte>(bufferBytes);
                result = await _clientWebSocket.ReceiveAsync(writeSegment, cancellationToken);
                DecodeFromBufferToStringBuilder(writeSegment, bufferChars, result.Count, encoding, sb);
            } while (!result.EndOfMessage);

            var response = sb.ToString();

            _messageReceived?.Invoke(this, new MessageReceivedEventArgs { Message = response, CancellationToken = cancellationToken });
            return response;
        }

#if NETSTANDARD2_0
        private static unsafe void DecodeFromBufferToStringBuilder(ArraySegment<byte> dataStream, char[] charBuffer, int byteCount, Encoding encoding, StringBuilder sb)
        {
            var decoder = encoding.GetDecoder();

            fixed (byte* bytePtr = dataStream.Array)
            fixed (char* charPtr = charBuffer)
            {
                int readChars = decoder.GetChars(bytePtr, byteCount, charPtr, encoding.GetMaxCharCount(byteCount), true);
                if (readChars > 0) sb.Append(charPtr, readChars);
            }
        }
#else
 private static unsafe void DecodeFromBufferToStringBuilder(ArraySegment<byte> dataStream, char[] charBuffer, int byteCount, Encoding encoding, StringBuilder sb)
        {
            var decoder = encoding.GetDecoder();

            fixed (byte* bytePtr = dataStream.Array)
            fixed (char* charPtr = charBuffer)
            {
                int readChars = decoder.GetChars(bytePtr, byteCount, charPtr, encoding.GetMaxCharCount(byteCount), true);
                if (readChars > 0) sb.Append(charBuffer, 0, readChars);
            }
        }
#endif
        /// <summary>Close connection and stops the message receiving Task.</summary>
        /// <remarks>
        /// The dispose method only disposes the unmanaged resorces and does not close the underlying connection or stops the long running tasks gracefully.
        /// Before this object is disposed the <see cref="M:WebSockets.WebSocketTextClient.DisconnectAsync" /> should be called.
        /// </remarks>
        public void Dispose()
        {
            if (_lazyReceiveContext.IsValueCreated) _lazyReceiveContext.Value.Release();
            _clientWebSocket.Dispose();
        }

        #region BufferingContext
        class BufferingContext : IDisposable
        {
            public readonly Encoding Encoding;
            public readonly byte[] BufferBytes;
            public readonly char[] BufferChars;
            public readonly StringBuilder StringBuilder;

            public BufferingContext(int bufferSize)
            {
                Encoding = Encoding.UTF8;
                BufferBytes = ArrayPool<byte>.Shared.Rent(bufferSize);
                BufferChars = ArrayPool<char>.Shared.Rent(Encoding.GetMaxCharCount(BufferBytes.Length));
                StringBuilder = new StringBuilder(bufferSize * 4);
            }

            public virtual void Dispose()
            {
                Release();
            }

            public void Release()
            {
                StringBuilder.Clear();
                ArrayPool<byte>.Shared.Return(BufferBytes);
                ArrayPool<char>.Shared.Return(BufferChars);
            }
        }

        class BufferingContextShared : BufferingContext
        {
            public BufferingContextShared(int bufferSize) : base(bufferSize)
            {
            }

            public override void Dispose()
            {
            }
        }
        #endregion
    }
}
