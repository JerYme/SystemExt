﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using static System.TimeSpan;
using static System.TimeSpanExt;
using static System.MonitorExt;
using static System.Threading.Tasks.TaskContinuationOptions;
// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable UnusedParameter.Local
// ReSharper disable PossibleNullReferenceException
// ReSharper disable UseObjectOrCollectionInitializer
// ReSharper disable RedundantCheckBeforeAssignment
// ReSharper disable UnusedMethodReturnValue.Local
// ReSharper disable ConvertToLambdaExpression

// NOTE: This file is shared between CoreFX and ASP.NET.  Be very thoughtful when changing it.

namespace System.Net.WebSockets.Managed
{
    /// <summary>A managed implementation of a web socket that sends and receives data via a <see cref="Stream"/>.</summary>
    /// <remarks>
    /// Thread-safety:
    /// - It's acceptable to call ReceiveAsync and SendAsync in parallel.  One of each may run concurrently.
    /// - It's acceptable to have a pending ReceiveAsync while CloseOutputAsync or CloseAsync is called.
    /// - Attemping to invoke any other operations in parallel may corrupt the instance.  Attempting to invoke
    ///   a send operation while another is in progress or a receive operation while another is in progress will
    ///   result in an exception.
    /// </remarks>
    internal sealed class ManagedWebSocket : IDisposable
    {
        /// <summary>Creates a <see cref="ManagedWebSocket"/> from a <see cref="Stream"/> connected to a websocket endpoint.</summary>
        /// <param name="stream">The connected Stream.</param>
        /// <param name="isServer">true if this is the server-side of the connection; false if this is the client-side of the connection.</param>
        /// <param name="subprotocol">The agreed upon subprotocol for the connection.</param>
        /// <param name="keepAliveInterval">The interval to use for keep-alive pings.</param>
        /// <param name="receiveBufferSize">The buffer size to use for received data.</param>
        /// <param name="receiveBuffer">Optional buffer to use for receives.</param>
        /// <returns>The created <see cref="ManagedWebSocket"/> instance.</returns>
        public static ManagedWebSocket CreateFromConnectedStream(
            Stream stream, bool isServer, string subprotocol, TimeSpan keepAliveInterval, int receiveBufferSize, ArraySegment<byte>? receiveBuffer = null)
        {
            return new ManagedWebSocket(stream, isServer, subprotocol, keepAliveInterval, receiveBufferSize, receiveBuffer);
        }

        /// <summary>Per-thread cached 4-byte mask byte array.</summary>
        [ThreadStatic]
        private static byte[] _headerMask;

        /// <summary>Thread-safe random number generator used to generate masks for each send.</summary>
        private static readonly RandomNumberGenerator s_random = RandomNumberGenerator.Create();
        /// <summary>Encoding for the payload of text messages: UTF8 encoding that throws if invalid bytes are discovered, per the RFC.</summary>
        private static readonly UTF8Encoding s_textEncoding = new UTF8Encoding(false, true);

        /// <summary>Valid states to be in when calling SendAsync.</summary>
        private static readonly WebSocketState[] s_validSendStates = { WebSocketState.Open, WebSocketState.CloseReceived };
        /// <summary>Valid states to be in when calling ReceiveAsync.</summary>
        private static readonly WebSocketState[] s_validReceiveStates = { WebSocketState.Open, WebSocketState.CloseSent };
        /// <summary>Valid states to be in when calling CloseOutputAsync.</summary>
        private static readonly WebSocketState[] s_validCloseOutputStates = { WebSocketState.Open, WebSocketState.CloseReceived };
        /// <summary>Valid states to be in when calling CloseAsync.</summary>
        private static readonly WebSocketState[] s_validCloseStates = { WebSocketState.Open, WebSocketState.CloseReceived, WebSocketState.CloseSent };

        /// <summary>The maximum size in bytes of a message frame header that includes mask bytes.</summary>
        private const int MaxMessageHeaderLength = 14;
        /// <summary>The maximum size of a control message payload.</summary>
        private const int MaxControlPayloadLength = 125;
        /// <summary>Length of the mask XOR'd with the payload data.</summary>
        private const int MaskLength = 4;

        /// <summary>The stream used to communicate with the remote server.</summary>
        private readonly Stream _stream;
        /// <summary>
        /// true if this is the server-side of the connection; false if it's client.
        /// This impacts masking behavior: clients always mask payloads they send and
        /// expect to always receive unmasked payloads, whereas servers always send
        /// unmasked payloads and expect to always receive masked payloads.
        /// </summary>
        private readonly bool _isServer;

        /// <summary>Timer used to send periodic pings to the server, at the interval specified</summary>
        private readonly Timer _keepAliveTimer;
        /// <summary>CancellationTokenSource used to abort all current and future operations when anything is canceled or any error occurs.</summary>
        private readonly CancellationTokenSource _abortSource = new CancellationTokenSource();
        /// <summary>Buffer used for reading data from the network.</summary>
        private byte[] _receiveBuffer;
        /// <summary>Gets whether the receive buffer came from the ArrayPool.</summary>
        private readonly bool _receiveBufferFromPool;
        /// <summary>
        /// Tracks the state of the validity of the UTF8 encoding of text payloads.  Text may be split across fragments.
        /// </summary>
        private readonly Utf8MessageState _utf8TextState = new Utf8MessageState();
        /// <summary>
        /// Semaphore used to ensure that calls to SendFrameAsync don't run concurrently.  While <see cref="_lastSendAsync"/>
        /// is used to fail if a caller tries to issue another SendAsync while a previous one is running, internally
        /// we use SendFrameAsync as an implementation detail, and it should not cause user requests to SendAsync to fail,
        /// nor should such internal usage be allowed to run concurrently with other internal usage or with SendAsync.
        /// </summary>
        private readonly SemaphoreSlim _sendFrameAsyncLock = new SemaphoreSlim(1, 1);

        // We maintain the current WebSocketState in _state.  However, we separately maintain _sentCloseFrame and _receivedCloseFrame
        // as there isn't a strict ordering between CloseSent and CloseReceived.  If we receive a close frame from the server, we need to
        // transition to CloseReceived even if we're currently in CloseSent, and if we send a close frame, we need to transition to
        // CloseSent even if we're currently in CloseReceived.

        /// <summary>true if Dispose has been called; otherwise, false.</summary>
        private bool _disposed;
        /// <summary>Whether we've ever sent a close frame.</summary>
        private bool _sentCloseFrame;
        /// <summary>Whether we've ever received a close frame.</summary>
        private bool _receivedCloseFrame;
        /// <summary>The reason for the close, as sent by the server, or null if not yet closed.</summary>
        private WebSocketCloseStatus? _closeStatus;
        /// <summary>A description of the close reason as sent by the server, or null if not yet closed.</summary>
        private string _closeStatusDescription;

        /// <summary>
        /// The last header received in a ReceiveAsync.  If ReceiveAsync got a header but then
        /// returned fewer bytes than was indicated in the header, subsequent ReceiveAsync calls
        /// will use the data from the header to construct the subsequent receive results, and
        /// the payload length in this header will be decremented to indicate the number of bytes
        /// remaining to be received for that header.  As a result, between fragments, the payload
        /// length in this header should be 0.
        /// </summary>
        private MessageHeader _lastReceiveHeader = new MessageHeader { Opcode = MessageOpcode.Text, Fin = true };
        /// <summary>The offset of the next available byte in the _receiveBuffer.</summary>
        private int _receiveBufferOffset;
        /// <summary>The number of bytes available in the _receiveBuffer.</summary>
        private int _receiveBufferCount;
        /// <summary>
        /// When dealing with partially read fragments of binary/text messages, a mask previously received may still
        /// apply, and the first new byte received may not correspond to the 0th position in the mask.  This value is
        /// the next offset into the mask that should be applied.
        /// </summary>
        private int _receivedMaskOffsetOffset;
        /// <summary>
        /// Temporary send buffer.  This should be released back to the ArrayPool once it's
        /// no longer needed for the current send operation.  It is stored as an instance
        /// field to minimize needing to pass it around and to avoid it becoming a field on
        /// various state machine objects.
        /// </summary>
        private byte[] _sendBuffer;
        /// <summary>
        /// Whether the last SendAsync had endOfMessage==false. We need to track this so that we
        /// can send the subsequent message with a continuation opcode if the last message was a fragment.
        /// </summary>
        private bool _lastSendWasFragment;
        /// <summary>
        /// The task returned from the last SendAsync operation to not complete synchronously.
        /// If this is not null and not completed when a subsequent SendAsync is issued, an exception occurs.
        /// </summary>
        private Task _lastSendAsync;
        /// <summary>
        /// The task returned from the last ReceiveAsync operation to not complete synchronously.
        /// If this is not null and not completed when a subsequent ReceiveAsync is issued, an exception occurs.
        /// </summary>
        private Task<WebSocketReceiveResult> _lastReceiveAsync;

        /// <summary>Lock used to protect update and check-and-update operations on _state.</summary>
        private object StateUpdateLock => _abortSource;
        /// <summary>
        /// We need to coordinate between receives and close operations happening concurrently, as a ReceiveAsync may
        /// be pending while a Close{Output}Async is issued, which itself needs to loop until a close frame is received.
        /// As such, we need thread-safety in the management of <see cref="_lastReceiveAsync"/>. 
        /// </summary>
        private object ReceiveAsyncLock => _utf8TextState; // some object, as we're simply lock'ing on it

        /// <summary>Initializes the websocket.</summary>
        /// <param name="stream">The connected Stream.</param>
        /// <param name="isServer">true if this is the server-side of the connection; false if this is the client-side of the connection.</param>
        /// <param name="subprotocol">The agreed upon subprotocol for the connection.</param>
        /// <param name="keepAliveInterval">The interval to use for keep-alive pings.</param>
        /// <param name="receiveBufferSize">The buffer size to use for received data.</param>
        /// <param name="receiveBuffer">Optional buffer to use for receives</param>
        private ManagedWebSocket(Stream stream, bool isServer, string subprotocol, TimeSpan keepAliveInterval, int receiveBufferSize, ArraySegment<byte>? receiveBuffer)
        {
            Debug.Assert(StateUpdateLock != null, $"Expected {nameof(StateUpdateLock)} to be non-null");
            Debug.Assert(ReceiveAsyncLock != null, $"Expected {nameof(ReceiveAsyncLock)} to be non-null");
            Debug.Assert(StateUpdateLock != ReceiveAsyncLock, "Locks should be different objects");

            Debug.Assert(stream != null, @"Expected non-null stream");
            Debug.Assert(stream.CanRead, @"Expected readable stream");
            Debug.Assert(stream.CanWrite, @"Expected writeable stream");
            Debug.Assert(keepAliveInterval == InfiniteTimeSpan || keepAliveInterval >= Zero, $"Invalid keepalive interval: {keepAliveInterval}");
            Debug.Assert(receiveBufferSize >= MaxMessageHeaderLength, $"Receive buffer size {receiveBufferSize} is too small");

            _stream = stream;
            _isServer = isServer;
            SubProtocol = subprotocol;

            // If we were provided with a buffer to use, use it, as long as it's big enough for our needs, and for simplicity
            // as long as we're not supposed to use only a portion of it.  If it doesn't meet our criteria, just create a new one.
            if (receiveBuffer.HasValue &&
                receiveBuffer.Value.Offset == 0 && receiveBuffer.Value.Count == receiveBuffer.Value.Array.Length &&
                receiveBuffer.Value.Count >= MaxMessageHeaderLength)
            {
                _receiveBuffer = receiveBuffer.Value.Array;
            }
            else
            {
                _receiveBufferFromPool = true;
                _receiveBuffer = new byte[Math.Max(receiveBufferSize, MaxMessageHeaderLength)];
            }

            // Set up the abort source so that if it's triggered, we transition the instance appropriately.
            _abortSource.Token.Register(s =>
            {
                var thisRef = (ManagedWebSocket)s;

                lock (thisRef.StateUpdateLock)
                {
                    WebSocketState state = thisRef.State;
                    if (state != WebSocketState.Closed && state != WebSocketState.Aborted)
                    {
                        thisRef.State = state != WebSocketState.None && state != WebSocketState.Connecting ?
                            WebSocketState.Aborted :
                            WebSocketState.Closed;
                    }
                }
            }, this);

            // Now that we're opened, initiate the keep alive timer to send periodic pings
            if (keepAliveInterval > Zero)
            {
                _keepAliveTimer = new Timer(s => ((ManagedWebSocket)s).SendKeepAliveFrameAsync(), this, keepAliveInterval, keepAliveInterval);
            }
        }

        public void Dispose()
        {
            lock (StateUpdateLock)
            {
                DisposeCore();
            }
        }

        private void DisposeCore()
        {
            Debug.Assert(IsEntered(StateUpdateLock), $"Expected {nameof(StateUpdateLock)} to be held");
            if (!_disposed)
            {
                _disposed = true;
                _keepAliveTimer?.Dispose();
                _stream?.Dispose();
                if (_receiveBufferFromPool)
                {
                    _receiveBuffer = null;
                }
                if (State < WebSocketState.Aborted)
                {
                    State = WebSocketState.Closed;
                }
            }
        }

        public WebSocketCloseStatus? CloseStatus => _closeStatus;

        public string CloseStatusDescription => _closeStatusDescription;

        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public string SubProtocol { get; }

        public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType != WebSocketMessageType.Text && messageType != WebSocketMessageType.Binary)
            {
                throw new ArgumentException(SR.Format(
                        SR.net_WebSockets_Argument_InvalidMessageType,
                        nameof(WebSocketMessageType.Close), nameof(SendAsync), nameof(WebSocketMessageType.Binary), nameof(WebSocketMessageType.Text), nameof(CloseOutputAsync)),
                    nameof(messageType));
            }
            WebSocketValidate.ValidateArraySegment(buffer, nameof(buffer));

            try
            {
                WebSocketValidate.ThrowIfInvalidState(State, _disposed, s_validSendStates);
                ThrowIfOperationInProgress(_lastSendAsync, "SendAsync");
            }
            catch (Exception exc)
            {
                var tcs = new TaskCompletionSource<bool>();
                tcs.SetException(exc);
                return tcs.Task;
            }

            MessageOpcode opcode =
                _lastSendWasFragment ? MessageOpcode.Continuation :
                    messageType == WebSocketMessageType.Binary ? MessageOpcode.Binary :
                        MessageOpcode.Text;

            Task t = SendFrameAsync(opcode, endOfMessage, buffer, cancellationToken);
            _lastSendWasFragment = !endOfMessage;
            _lastSendAsync = t;
            return t;
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            WebSocketValidate.ValidateArraySegment(buffer, nameof(buffer));

            try
            {
                WebSocketValidate.ThrowIfInvalidState(State, _disposed, s_validReceiveStates);

                Debug.Assert(!IsEntered(StateUpdateLock), $"{nameof(StateUpdateLock)} must never be held when acquiring {nameof(ReceiveAsyncLock)}");
                lock (ReceiveAsyncLock) // synchronize with receives in CloseAsync
                {
                    ThrowIfOperationInProgress(_lastReceiveAsync, "ReceiveAsync");
                    var t = ReceiveAsyncPrivate(buffer, cancellationToken);
                    _lastReceiveAsync = t;
                    return t;
                }
            }
            catch (Exception exc)
            {
                var tcs = new TaskCompletionSource<WebSocketReceiveResult>();
                tcs.SetException(exc);
                return tcs.Task;
            }
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            WebSocketValidate.ValidateCloseStatus(closeStatus, statusDescription);

            try
            {
                WebSocketValidate.ThrowIfInvalidState(State, _disposed, s_validCloseStates);
            }
            catch (Exception exc)
            {
                var tcs = new TaskCompletionSource<bool>();
                tcs.SetException(exc);
                return tcs.Task;
            }

            return CloseAsyncPrivate(closeStatus, statusDescription, cancellationToken);
        }

        public Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            WebSocketValidate.ValidateCloseStatus(closeStatus, statusDescription);

            try
            {
                WebSocketValidate.ThrowIfInvalidState(State, _disposed, s_validCloseOutputStates);
            }
            catch (Exception exc)
            {
                var tcs = new TaskCompletionSource<bool>();
                tcs.SetException(exc);
                return tcs.Task;
            }

            return SendCloseFrameAsync(closeStatus, statusDescription, cancellationToken);
        }

        public void Abort()
        {
            _abortSource.Cancel();
            Dispose(); // forcibly tear down connection
        }

        /// <summary>Sends a websocket frame to the network.</summary>
        /// <param name="opcode">The opcode for the message.</param>
        /// <param name="endOfMessage">The value of the FIN bit for the message.</param>
        /// <param name="payloadBuffer">The buffer containing the payload data fro the message.</param>
        /// <param name="cancellationToken">The CancellationToken to use to cancel the websocket.</param>
        private Task SendFrameAsync(MessageOpcode opcode, bool endOfMessage, ArraySegment<byte> payloadBuffer, CancellationToken cancellationToken)
        {
            // TODO: #4900 SendFrameAsync should in theory typically complete synchronously, making it fast and allocation free.
            // However, due to #4900, it almost always yields, resulting in all of the allocations involved in an method
            // yielding, e.g. the boxed state machine, the Action delegate, the MoveNextRunner, and the resulting Task, plus it's
            // common that the awaited operation completes so fast after the await that we may end up allocating an AwaitTaskContinuation
            // inside of the TaskAwaiter.  Since SendFrameAsync is such a core code path, until that can be fixed, we put some
            // optimizations in place to avoid a few of those expenses, at the expense of more complicated code; for the common case,
            // this code has fewer than half the number and size of allocations.  If/when that issue is fixed, this method should be deleted
            // and replaced by SendFrameFallbackAsync, which is the same logic but in a much more easily understand flow.

            // If a cancelable cancellation token was provided, that would require registering with it, which means more state we have to
            // pass around (the CancellationTokenRegistration), so if it is cancelable, just immediately go to the fallback path.
            // Similarly, it should be rare that there are multiple outstanding calls to SendFrameAsync, but if there are, again
            // fall back to the fallback path.
            return cancellationToken.CanBeCanceled || !_sendFrameAsyncLock.Wait(0) ?
                SendFrameFallbackAsync(opcode, endOfMessage, payloadBuffer, cancellationToken) :
                SendFrameLockAcquiredNonCancelableAsync(opcode, endOfMessage, payloadBuffer);
        }

        /// <summary>Sends a websocket frame to the network. The caller must hold the sending lock.</summary>
        /// <param name="opcode">The opcode for the message.</param>
        /// <param name="endOfMessage">The value of the FIN bit for the message.</param>
        /// <param name="payloadBuffer">The buffer containing the payload data fro the message.</param>
        private Task SendFrameLockAcquiredNonCancelableAsync(MessageOpcode opcode, bool endOfMessage, ArraySegment<byte> payloadBuffer)
        {
            Debug.Assert(_sendFrameAsyncLock.CurrentCount == 0, "Caller should hold the _sendFrameAsyncLock");

            // If we get here, the cancellation token is not cancelable so we don't have to worry about it,
            // and we own the semaphore, so we don't need to asynchronously wait for it.
            Task writeTask;
            bool releaseSemaphoreAndSendBuffer = true;
            try
            {
                // Write the payload synchronously to the buffer, then write that buffer out to the network.
                int sendBytes = WriteFrameToSendBuffer(opcode, endOfMessage, payloadBuffer);
                writeTask = _stream.WriteAsync(_sendBuffer, 0, sendBytes, CancellationToken.None);

                // If the operation happens to complete synchronously (or, more specifically, by
                // the time we get from the previous line to here, release the semaphore, propagate
                // exceptions, and we're done.
                if (writeTask.IsCompleted)
                {
                    return TaskEx.FromResult(true);
                }

                // Up until this point, if an exception occurred (such as when accessing _stream or when
                // calling GetResult), we want to release the semaphore and the send buffer. After this point,
                // both need to be held until writeTask completes.
                releaseSemaphoreAndSendBuffer = false;
            }
            catch (Exception exc)
            {
                var tcs = new TaskCompletionSource<bool>();
                tcs.SetException(State == WebSocketState.Aborted ?
                    CreateOperationCanceledException(exc) :
                    new WebSocketException(WebSocketError.ConnectionClosedPrematurely, exc));
                return tcs.Task;
            }
            finally
            {
                if (releaseSemaphoreAndSendBuffer)
                {
                    _sendFrameAsyncLock.Release();
                    ReleaseSendBuffer();
                }
            }

            // The write was not yet completed.  Create and return a continuation that will
            // release the semaphore and translate any exception that occurred.
            return writeTask.ContinueWith((t) =>
            {
                var thisRef = this;
                thisRef._sendFrameAsyncLock.Release();
                thisRef.ReleaseSendBuffer();

                try
                {
                    if (!t.IsCompleted) t.Wait(InfiniteTimeSpan);
                }
                catch (Exception exc)
                {
                    throw thisRef.State == WebSocketState.Aborted ?
                        CreateOperationCanceledException(exc) :
                        new WebSocketException(WebSocketError.ConnectionClosedPrematurely, exc);
                }
            }, CancellationToken.None, ExecuteSynchronously, TaskScheduler.Default);
        }

        private Task SendFrameFallbackAsync(MessageOpcode opcode, bool endOfMessage, ArraySegment<byte> payloadBuffer, CancellationToken cancellationToken)
        {
            _sendFrameAsyncLock.WaitAsync();
            int sendBytes = WriteFrameToSendBuffer(opcode, endOfMessage, payloadBuffer);
            var disposable = cancellationToken.Register(s => ((ManagedWebSocket)s).Abort(), this);
            return _stream.WriteAsync(_sendBuffer, 0, sendBytes, cancellationToken).UsingWith(disposable)
                .CatchWith((Exception exc) =>
                {
                    throw State == WebSocketState.Aborted ? CreateOperationCanceledException(exc, cancellationToken) : new WebSocketException(WebSocketError.ConnectionClosedPrematurely, exc);
                })
                .FinallyWith(() =>
                {
                    _sendFrameAsyncLock.Release();
                    ReleaseSendBuffer();
                });
        }

        /// <summary>Writes a frame into the send buffer, which can then be sent over the network.</summary>
        private int WriteFrameToSendBuffer(MessageOpcode opcode, bool endOfMessage, ArraySegment<byte> payloadBuffer)
        {
            // Ensure we have a _sendBuffer.
            AllocateSendBuffer(payloadBuffer.Count + MaxMessageHeaderLength);

            // Write the message header data to the buffer.
            int headerLength;
            int? maskOffset = null;
            if (_isServer)
            {
                // The server doesn't send a mask, so the mask offset returned by WriteHeader
                // is actually the end of the header.
                headerLength = WriteHeader(opcode, _sendBuffer, payloadBuffer, endOfMessage, false);
            }
            else
            {
                // We need to know where the mask starts so that we can use the mask to manipulate the payload data,
                // and we need to know the total length for sending it on the wire.
                maskOffset = WriteHeader(opcode, _sendBuffer, payloadBuffer, endOfMessage, true);
                headerLength = maskOffset.GetValueOrDefault() + MaskLength;
            }

            // Write the payload
            if (payloadBuffer.Count > 0)
            {
                Buffer.BlockCopy(payloadBuffer.Array, payloadBuffer.Offset, _sendBuffer, headerLength, payloadBuffer.Count);

                // If we added a mask to the header, XOR the payload with the mask.  We do the manipulation in the send buffer so as to avoid
                // changing the data in the caller-supplied payload buffer.
                if (maskOffset.HasValue)
                {
                    ApplyMask(_sendBuffer, headerLength, _sendBuffer, maskOffset.Value, 0, payloadBuffer.Count);
                }
            }

            // Return the number of bytes in the send buffer
            return headerLength + payloadBuffer.Count;
        }

        private void SendKeepAliveFrameAsync()
        {
            bool acquiredLock = _sendFrameAsyncLock.Wait(0);
            if (acquiredLock)
            {
                // This exists purely to keep the connection alive; don't wait for the result, and ignore any failures.
                // The call will handle releasing the lock.
                SendFrameLockAcquiredNonCancelableAsync(MessageOpcode.Ping, true, new ArraySegment<byte>(new byte[0]));
            }
            else
            {
                // If the lock is already held, something is already getting sent,
                // so there's no need to send a keep-alive ping.
            }
        }

        private static int WriteHeader(MessageOpcode opcode, byte[] sendBuffer, ArraySegment<byte> payload, bool endOfMessage, bool useMask)
        {
            // Client header format:
            // 1 bit - FIN - 1 if this is the final fragment in the message (it could be the only fragment), otherwise 0
            // 1 bit - RSV1 - Reserved - 0
            // 1 bit - RSV2 - Reserved - 0
            // 1 bit - RSV3 - Reserved - 0
            // 4 bits - Opcode - How to interpret the payload
            //     - 0x0 - continuation
            //     - 0x1 - text
            //     - 0x2 - binary
            //     - 0x8 - connection close
            //     - 0x9 - ping
            //     - 0xA - pong
            //     - (0x3 to 0x7, 0xB-0xF - reserved)
            // 1 bit - Masked - 1 if the payload is masked, 0 if it's not.  Must be 1 for the client
            // 7 bits, 7+16 bits, or 7+64 bits - Payload length
            //     - For length 0 through 125, 7 bits storing the length
            //     - For lengths 126 through 2^16, 7 bits storing the value 126, followed by 16 bits storing the length
            //     - For lengths 2^16+1 through 2^64, 7 bits storing the value 127, followed by 64 bytes storing the length
            // 0 or 4 bytes - Mask, if Masked is 1 - random value XOR'd with each 4 bytes of the payload, round-robin
            // Length bytes - Payload data

            Debug.Assert(sendBuffer.Length >= MaxMessageHeaderLength, $"Expected sendBuffer to be at least {MaxMessageHeaderLength}, got {sendBuffer.Length}");

            sendBuffer[0] = (byte)opcode; // 4 bits for the opcode
            if (endOfMessage)
            {
                sendBuffer[0] |= 0x80; // 1 bit for FIN
            }

            // Store the payload length.
            int maskOffset;
            if (payload.Count <= 125)
            {
                sendBuffer[1] = (byte)payload.Count;
                maskOffset = 2; // no additional payload length
            }
            else if (payload.Count <= ushort.MaxValue)
            {
                sendBuffer[1] = 126;
                sendBuffer[2] = (byte)(payload.Count / 256);
                sendBuffer[3] = unchecked((byte)payload.Count);
                maskOffset = 2 + sizeof(ushort); // additional 2 bytes for 16-bit length
            }
            else
            {
                sendBuffer[1] = 127;
                int length = payload.Count;
                for (int i = 9; i >= 2; i--)
                {
                    sendBuffer[i] = unchecked((byte)length);
                    length = length / 256;
                }
                maskOffset = 2 + sizeof(ulong); // additional 8 bytes for 64-bit length
            }

            if (useMask)
            {
                // Generate the mask.
                sendBuffer[1] |= 0x80;
                WriteRandomMask(sendBuffer, maskOffset);
            }

            // Return the position of the mask.
            return maskOffset;
        }

        /// <summary>Writes a 4-byte random mask to the specified buffer at the specified offset.</summary>
        /// <param name="buffer">The buffer to which to write the mask.</param>
        /// <param name="offset">The offset into the buffer at which to write the mask.</param>
        private static void WriteRandomMask(byte[] buffer, int offset)
        {
            byte[] mask = _headerMask ?? (_headerMask = new byte[MaskLength]);
            Debug.Assert(mask.Length == MaskLength, $"Expected mask of length {MaskLength}, got {mask.Length}");
            s_random.GetBytes(mask);
            Buffer.BlockCopy(mask, 0, buffer, offset, MaskLength);
        }


        /// <summary>
        /// Receive the next text, binary, continuation, or close message, returning information about it and
        /// writing its payload into the supplied buffer.  Other control messages may be consumed and processed
        /// as part of this operation, but data about them will not be returned.
        /// </summary>
        /// <param name="payloadBuffer">The buffer into which payload data should be written.</param>
        /// <param name="cancellationToken">The CancellationToken used to cancel the websocket.</param>
        /// <returns>Information about the received message.</returns>
        private Task<WebSocketReceiveResult> ReceiveAsyncPrivate(ArraySegment<byte> payloadBuffer, CancellationToken cancellationToken)
        {
            // This is a long method.  While splitting it up into pieces would arguably help with readability, doing so would
            // also result in more allocations, as each async method that yields ends up with multiple allocations.  The impact
            // of those allocations is amortized across all of the awaits in the method, and since we generally expect a receive
            // operation to require at most a single yield (while waiting for data to arrive), it's more efficient to have
            // everything in the one method.  We do separate out pieces for handling close and ping/pong messages, as we expect
            // those to be much less frequent (e.g. we should only get one close per websocket), and thus we can afford to pay
            // a bit more for readability and maintainability.

            CancellationTokenRegistration registration = cancellationToken.Register(s => ((ManagedWebSocket)s).Abort(), this);



            // in case we get control frames that should be ignored from the user's perspective
            return TaskEx.AsyncLoopTask(() =>
                {

                    // Get the last received header.  If its payload length is non-zero, that means we previously
                    // received the header but were only able to read a part of the fragment, so we should skip
                    // reading another header and just proceed to use that same header and read more data associated
                    // with it.  If instead its payload length is zero, then we've completed the processing of
                    // thta message, and we should read the next header.
                    MessageHeader header = _lastReceiveHeader;
                    return HandleReceivedHeaderAsync(header, cancellationToken)
                        .ContinueWith(t1 =>
                        {
                            // If the header represents a ping or a pong, it's a control message meant
                            // to be transparent to the user, so handle it and then loop around to read again.
                            // Alternatively, if it's a close message, handle it and exit.
                            if (header.Opcode == MessageOpcode.Ping || header.Opcode == MessageOpcode.Pong)
                            {
                                return HandleReceivedPingPongAsync(header, cancellationToken).ContinueWith(t => Flow<WebSocketReceiveResult>.Continue(), cancellationToken);
                            }
                            if (header.Opcode == MessageOpcode.Close)
                            {
                                return HandleReceivedCloseAsync(header, cancellationToken).ContinueWith(t => Flow<WebSocketReceiveResult>.Return(t.Result), cancellationToken);
                            }

                            // If this is a continuation, replace the opcode with the one of the message it's continuing
                            if (header.Opcode == MessageOpcode.Continuation)
                            {
                                header.Opcode = _lastReceiveHeader.Opcode;
                            }

                            // The message should now be a binary or text message.  Handle it by reading the payload and returning the contents.
                            Debug.Assert(header.Opcode == MessageOpcode.Binary || header.Opcode == MessageOpcode.Text, $"Unexpected opcode {header.Opcode}");

                            // If there's no data to read, return an appropriate result.
                            int bytesToRead = (int)Math.Min(payloadBuffer.Count, header.PayloadLength);
                            if (bytesToRead == 0)
                            {
                                _lastReceiveHeader = header;
                                return TaskEx.FromFlow(new WebSocketReceiveResult(
                                    0,
                                    header.Opcode == MessageOpcode.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                                    header.PayloadLength == 0 && header.Fin));
                            }

                            // Otherwise, read as much of the payload as we can efficiently, and upate the header to reflect how much data
                            // remains for future reads.

                            var subChain1 = _receiveBufferCount == 0
                                ? EnsureBufferContainsAsync(1, cancellationToken, false)
                                : TaskEx.TaskCompleted;

                            return subChain1.ContinueWith(t2 =>
                            {
                                int bytesToCopy = Math.Min(bytesToRead, _receiveBufferCount);
                                if (_isServer)
                                {
                                    _receivedMaskOffsetOffset = ApplyMask(_receiveBuffer, _receiveBufferOffset, header.Mask, _receivedMaskOffsetOffset, bytesToCopy);
                                }
                                Buffer.BlockCopy(_receiveBuffer, _receiveBufferOffset, payloadBuffer.Array, payloadBuffer.Offset, bytesToCopy);
                                ConsumeFromBuffer(bytesToCopy);
                                header.PayloadLength -= bytesToCopy;

                                // If this a text message, validate that it contains valid UTF8.
                                var subChain2 = header.Opcode == MessageOpcode.Text &&
                                                !TryValidateUtf8(new ArraySegment<byte>(payloadBuffer.Array, payloadBuffer.Offset, bytesToCopy), header.Fin, _utf8TextState)
                                    ? CloseWithReceiveErrorAndThrowAsync(WebSocketCloseStatus.InvalidPayloadData, WebSocketError.Faulted, cancellationToken)
                                    : TaskEx.TaskCompleted;

                                return subChain2.ContinueWith(t3 =>
                                    {
                                        _lastReceiveHeader = header;
                                        var r = new WebSocketReceiveResult(
                                            bytesToCopy,
                                            header.Opcode == MessageOpcode.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                                            bytesToCopy == 0 || (header.Fin && header.PayloadLength == 0));
                                        return Flow<WebSocketReceiveResult>.Return(r);
                                    }, cancellationToken
                                );

                            }, cancellationToken).Unwrap();
                        }, cancellationToken).Unwrap();
                })
                .CatchWith((Exception exc) =>
                {
                    if (State == WebSocketState.Aborted)
                    {
                        throw new OperationCanceledException(nameof(WebSocketState.Aborted), exc);
                    }
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, exc);
                })
                .UsingWith(registration);
        }

        private Task HandleReceivedHeaderAsync(MessageHeader header, CancellationToken cancellationToken)
        {
            var chain = TaskEx.TaskCompleted;
            if (header.PayloadLength != 0) return chain;

            if (_receiveBufferCount < (_isServer ? (MaxMessageHeaderLength - MaskLength) : MaxMessageHeaderLength))
            {
                // Make sure we have the first two bytes, which includes the start of the payload length.
                if (_receiveBufferCount < 2)
                {
                    chain = EnsureBufferContainsAsync(2, cancellationToken);
                }

                chain = chain.ContinueWith(t1 =>
                {
                    var chain1 = TaskEx.TaskCompleted;
                    // Then make sure we have the full header based on the payload length.
                    // If this is the server, we also need room for the received mask.
                    long payloadLength = _receiveBuffer[_receiveBufferOffset + 1] & 0x7F;
                    if (_isServer || payloadLength > 125)
                    {
                        int minNeeded =
                            2 +
                            (_isServer ? MaskLength : 0) +
                            (payloadLength <= 125 ? 0 : payloadLength == 126 ? sizeof(ushort) : sizeof(ulong)); // additional 2 or 8 bytes for 16-bit or 64-bit length
                        chain1 = chain1.ContinueWith((t2) => EnsureBufferContainsAsync(minNeeded, cancellationToken), cancellationToken).Unwrap();
                    }
                    return chain1;
                }, cancellationToken).Unwrap();

            }

            if (TryParseMessageHeaderFromReceiveBuffer(out header)) return chain;

            return chain
                .ContinueWith(task => CloseWithReceiveErrorAndThrowAsync(WebSocketCloseStatus.ProtocolError, WebSocketError.Faulted, cancellationToken), cancellationToken).Unwrap()
                .ContinueWith(task => _receivedMaskOffsetOffset = 0, cancellationToken);
        }

        /// <summary>Processes a received close message.</summary>
        /// <param name="header">The message header.</param>
        /// <param name="cancellationToken">The cancellation token to use to cancel the websocket.</param>
        /// <returns>The received result message.</returns>
        private Task<WebSocketReceiveResult> HandleReceivedCloseAsync(
            MessageHeader header, CancellationToken cancellationToken)
        {
            lock (StateUpdateLock)
            {
                _receivedCloseFrame = true;
                if (State < WebSocketState.CloseReceived)
                {
                    State = WebSocketState.CloseReceived;
                }
            }

            WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;
            string closeStatusDescription = string.Empty;

            var chain = TaskEx.TaskCompleted;

            // Handle any payload by parsing it into the close status and description.
            if (header.PayloadLength == 1)
            {
                // The close payload length can be 0 or >= 2, but not 1.
                chain = chain.ContinueWith(task => CloseWithReceiveErrorAndThrowAsync(WebSocketCloseStatus.ProtocolError, WebSocketError.Faulted, cancellationToken), cancellationToken).Unwrap();
            }
            else if (header.PayloadLength >= 2)
            {
                if (_receiveBufferCount < header.PayloadLength)
                {
                    chain = chain.ContinueWith(task => EnsureBufferContainsAsync((int)header.PayloadLength, cancellationToken), cancellationToken).Unwrap();
                }

                chain = chain.ContinueWith(t0 =>
                {
                    if (_isServer)
                    {
                        ApplyMask(_receiveBuffer, _receiveBufferOffset, header.Mask, 0, header.PayloadLength);
                    }

                    var subchain = TaskEx.TaskCompleted;

                    closeStatus = (WebSocketCloseStatus)(_receiveBuffer[_receiveBufferOffset] << 8 | _receiveBuffer[_receiveBufferOffset + 1]);
                    if (!IsValidCloseStatus(closeStatus))
                    {
                        subchain = subchain.ContinueWith(t1 => CloseWithReceiveErrorAndThrowAsync(WebSocketCloseStatus.ProtocolError, WebSocketError.Faulted, cancellationToken), cancellationToken).Unwrap();
                    }

                    if (header.PayloadLength > 2)
                    {
                        try
                        {
                            closeStatusDescription = s_textEncoding.GetString(_receiveBuffer, _receiveBufferOffset + 2, (int)header.PayloadLength - 2);
                        }
                        catch (DecoderFallbackException exc)
                        {
                            subchain = subchain.ContinueWith(t1 => CloseWithReceiveErrorAndThrowAsync(WebSocketCloseStatus.ProtocolError, WebSocketError.Faulted, cancellationToken, exc), cancellationToken).Unwrap();
                        }
                    }
                    ConsumeFromBuffer((int)header.PayloadLength);
                    return subchain;
                }, cancellationToken).Unwrap();


            }

            return chain.ContinueWith(task =>
            {
                // Store the close status and description onto the instance.
                _closeStatus = closeStatus;
                _closeStatusDescription = closeStatusDescription;

                // And return them as part of the result message.
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, closeStatus, closeStatusDescription);
            }, AttachedToParent);
        }

        /// <summary>Processes a received ping or pong message.</summary>
        /// <param name="header">The message header.</param>
        /// <param name="cancellationToken">The cancellation token to use to cancel the websocket.</param>
        private Task HandleReceivedPingPongAsync(MessageHeader header, CancellationToken cancellationToken)
        {
            var chain = TaskEx.TaskCompleted;

            // Consume any (optional) payload associated with the ping/pong.
            if (header.PayloadLength > 0 && _receiveBufferCount < header.PayloadLength)
            {
                chain = chain.ContinueWith(task => EnsureBufferContainsAsync((int)header.PayloadLength, cancellationToken), cancellationToken).Unwrap();
            }

            chain = chain.ContinueWith(task =>
            {
                if (header.Opcode == MessageOpcode.Ping)
                {
                    if (_isServer)
                    {
                        ApplyMask(_receiveBuffer, _receiveBufferOffset, header.Mask, 0, header.PayloadLength);
                    }

                    return SendFrameAsync(MessageOpcode.Pong, true, new ArraySegment<byte>(_receiveBuffer, _receiveBufferOffset, (int)header.PayloadLength), cancellationToken);
                }
                return TaskEx.TaskCompleted;
            }, AttachedToParent).Unwrap();


            chain = chain.ContinueWith(task =>
            {
                // If this was a ping, send back a pong response.
                if (header.Opcode == MessageOpcode.Ping)
                {
                    if (_isServer)
                    {
                        ApplyMask(_receiveBuffer, _receiveBufferOffset, header.Mask, 0, header.PayloadLength);
                    }

                    return SendFrameAsync(MessageOpcode.Pong, true, new ArraySegment<byte>(_receiveBuffer, _receiveBufferOffset, (int)header.PayloadLength), cancellationToken);
                }
                return TaskEx.TaskCompleted;
            }, AttachedToParent).Unwrap();

            return chain.ContinueWith(task =>
            {
                // Regardless of whether it was a ping or pong, we no longer need the payload.
                if (header.PayloadLength > 0)
                {
                    ConsumeFromBuffer((int)header.PayloadLength);
                }
            }, AttachedToParent);
        }

        /// <summary>Check whether a close status is valid according to the RFC.</summary>
        /// <param name="closeStatus">The status to validate.</param>
        /// <returns>true if the status if valid; otherwise, false.</returns>
        private static bool IsValidCloseStatus(WebSocketCloseStatus closeStatus)
        {
            // 0-999: "not used"
            // 1000-2999: reserved for the protocol; we need to check individual codes manually
            // 3000-3999: reserved for use by higher-level code
            // 4000-4999: reserved for private use
            // 5000-: not mentioned in RFC

            if (closeStatus < (WebSocketCloseStatus)1000 || closeStatus >= (WebSocketCloseStatus)5000)
            {
                return false;
            }

            if (closeStatus >= (WebSocketCloseStatus)3000)
            {
                return true;
            }

            switch (closeStatus) // check for the 1000-2999 range known codes
            {
                case WebSocketCloseStatus.EndpointUnavailable:
                case WebSocketCloseStatus.InternalServerError:
                case WebSocketCloseStatus.InvalidMessageType:
                case WebSocketCloseStatus.InvalidPayloadData:
                case WebSocketCloseStatus.MandatoryExtension:
                case WebSocketCloseStatus.MessageTooBig:
                case WebSocketCloseStatus.NormalClosure:
                case WebSocketCloseStatus.PolicyViolation:
                case WebSocketCloseStatus.ProtocolError:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Send a close message to the server and throw an exception, in response to getting bad data from the server.</summary>
        /// <param name="closeStatus">The close status code to use.</param>
        /// <param name="error">The error reason.</param>
        /// <param name="cancellationToken">The CancellationToken used to cancel the websocket.</param>
        /// <param name="innerException">An optional inner exception to include in the thrown exception.</param>
        private Task CloseWithReceiveErrorAndThrowAsync(WebSocketCloseStatus closeStatus, WebSocketError error, CancellationToken cancellationToken, Exception innerException = null)
        {
            // Close the connection if it hasn't already been closed
            if (_sentCloseFrame)
            {
                _receiveBufferCount = 0;
                // Let the caller know we've failed
                throw new WebSocketException(error, innerException);
            }

            // Dump our receive buffer; we're in a bad state to do any further processing
            return CloseOutputAsync(closeStatus, string.Empty, cancellationToken)
                .ContinueWith(t =>
                    {
                        // Dump our receive buffer; we're in a bad state to do any further processing
                        _receiveBufferCount = 0;
                        // Let the caller know we've failed
                        throw new WebSocketException(error, innerException);
                    }
                    , cancellationToken);
        }

        /// <summary>Parses a message header from the buffer.  This assumes the header is in the buffer.</summary>
        /// <param name="resultHeader">The read header.</param>
        /// <returns>true if a header was read; false if the header was invalid.</returns>
        private bool TryParseMessageHeaderFromReceiveBuffer(out MessageHeader resultHeader)
        {
            Debug.Assert(_receiveBufferCount >= 2, @"Expected to at least have the first two bytes of the header.");

            var header = new MessageHeader();

            header.Fin = (_receiveBuffer[_receiveBufferOffset] & 0x80) != 0;
            bool reservedSet = (_receiveBuffer[_receiveBufferOffset] & 0x70) != 0;
            header.Opcode = (MessageOpcode)(_receiveBuffer[_receiveBufferOffset] & 0xF);

            bool masked = (_receiveBuffer[_receiveBufferOffset + 1] & 0x80) != 0;
            header.PayloadLength = _receiveBuffer[_receiveBufferOffset + 1] & 0x7F;

            ConsumeFromBuffer(2);

            // Read the remainder of the payload length, if necessary
            if (header.PayloadLength == 126)
            {
                Debug.Assert(_receiveBufferCount >= 2, @"Expected to have two bytes for the payload length.");
                header.PayloadLength = (_receiveBuffer[_receiveBufferOffset] << 8) | _receiveBuffer[_receiveBufferOffset + 1];
                ConsumeFromBuffer(2);
            }
            else if (header.PayloadLength == 127)
            {
                Debug.Assert(_receiveBufferCount >= 8, @"Expected to have eight bytes for the payload length.");
                header.PayloadLength = 0;
                for (int i = 0; i < 8; i++)
                {
                    header.PayloadLength = (header.PayloadLength << 8) | _receiveBuffer[_receiveBufferOffset + i];
                }
                ConsumeFromBuffer(8);
            }

            bool shouldFail = reservedSet;
            if (masked)
            {
                if (!_isServer)
                {
                    shouldFail = true;
                }
                header.Mask = CombineMaskBytes(_receiveBuffer, _receiveBufferOffset);

                // Consume the mask bytes
                ConsumeFromBuffer(4);
            }

            // Do basic validation of the header
            switch (header.Opcode)
            {
                case MessageOpcode.Continuation:
                    if (_lastReceiveHeader.Fin)
                    {
                        // Can't continue from a final message
                        shouldFail = true;
                    }
                    break;

                case MessageOpcode.Binary:
                case MessageOpcode.Text:
                    if (!_lastReceiveHeader.Fin)
                    {
                        // Must continue from a non-final message
                        shouldFail = true;
                    }
                    break;

                case MessageOpcode.Close:
                case MessageOpcode.Ping:
                case MessageOpcode.Pong:
                    if (header.PayloadLength > MaxControlPayloadLength || !header.Fin)
                    {
                        // Invalid control messgae
                        shouldFail = true;
                    }
                    break;

                default:
                    // Unknown opcode
                    shouldFail = true;
                    break;
            }

            // Return the read header
            resultHeader = header;
            return !shouldFail;
        }

        /// <summary>Send a close message, then receive until we get a close response message.</summary>
        /// <param name="closeStatus">The close status to send.</param>
        /// <param name="statusDescription">The close status description to send.</param>
        /// <param name="cancellationToken">The CancellationToken to use to cancel the websocket.</param>
        private Task CloseAsyncPrivate(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            var chain = TaskEx.TaskCompleted;

            // Send the close message.  Skip sending a close frame if we're currently in a CloseSent state,
            // for example having just done a CloseOutputAsync.
            if (!_sentCloseFrame)
            {
                chain = chain.ContinueWith(task => SendCloseFrameAsync(closeStatus, statusDescription, cancellationToken), cancellationToken).Unwrap();
            }

            byte[] closeBuffer = null;
            return chain.ContinueWith(task =>
            {
                // We should now either be in a CloseSent case (because we just sent one), or in a CloseReceived state, in case
                // there was a concurrent receive that ended up handling an immediate close frame response from the server.
                // Of course it could also be Aborted if something happened concurrently to cause things to blow up.
                Debug.Assert(
                    State == WebSocketState.CloseSent ||
                    State == WebSocketState.CloseReceived ||
                    State == WebSocketState.Aborted,
                    $"Unexpected state {State}.");

                // Wait until we've received a close response
                closeBuffer = new byte[MaxMessageHeaderLength + MaxControlPayloadLength];

                return TaskEx.AsyncLoopTask(() =>
                {
                    if (_receivedCloseFrame) return TaskEx.TaskBreak;
                    Debug.Assert(!IsEntered(StateUpdateLock), $"{nameof(StateUpdateLock)} must never be held when acquiring {nameof(ReceiveAsyncLock)}");
                    Task<WebSocketReceiveResult> receiveTask;
                    if (!TryAcquireReceiveTask(cancellationToken, closeBuffer, out receiveTask)) return TaskEx.TaskBreak;

                    // Wait for whatever receive task we have.  We'll then loop around again to re-check our state.
                    Debug.Assert(receiveTask != null);
                    return receiveTask.ContinueWith(tmp => true, cancellationToken);
                });

            }, cancellationToken).Unwrap()
            .FinallyWith(() => ArrayPool<byte>.Shared.Return(closeBuffer))
            .ContinueWith(task =>
                {
                    // We're closed.  Close the connection and update the status.
                    lock (StateUpdateLock)
                    {
                        DisposeCore();
                        if (State < WebSocketState.Closed)
                        {
                            State = WebSocketState.Closed;
                        }
                    }
                }, cancellationToken);
        }

        private bool TryAcquireReceiveTask(CancellationToken cancellationToken, byte[] closeBuffer, out Task<WebSocketReceiveResult> receiveTask)
        {
            lock (ReceiveAsyncLock)
            {
                // Now that we're holding the ReceiveAsyncLock, double-check that we've not yet received the close frame.
                // It could have been received between our check above and now due to a concurrent receive completing.
                if (_receivedCloseFrame)
                {
                    receiveTask = null;
                    return false;
                }

                // We've not yet processed a received close frame, which means we need to wait for a received close to complete.
                // There may already be one in flight, in which case we want to just wait for that one rather than kicking off
                // another (we don't support concurrent receive operations).  We need to kick off a new receive if either we've
                // never issued a receive or if the last issued receive completed for reasons other than a close frame.  There is
                // a race condition here, e.g. if there's a in-flight receive that completes after we check, but that's fine: worst
                // case is we then await it, find that it's not what we need, and try again.
                receiveTask = _lastReceiveAsync;
                if (receiveTask == null ||
                    (receiveTask.Status == TaskStatus.RanToCompletion && receiveTask.Result.MessageType != WebSocketMessageType.Close))
                {
                    _lastReceiveAsync = receiveTask = ReceiveAsyncPrivate(new ArraySegment<byte>(closeBuffer), cancellationToken);
                }
            }
            return true;
        }

        /// <summary>Sends a close message to the server.</summary>
        /// <param name="closeStatus">The close status to send.</param>
        /// <param name="closeStatusDescription">The close status description to send.</param>
        /// <param name="cancellationToken">The CancellationToken to use to cancel the websocket.</param>
        private Task SendCloseFrameAsync(WebSocketCloseStatus closeStatus, string closeStatusDescription, CancellationToken cancellationToken)
        {
            // Close payload is two bytes containing the close status followed by a UTF8-encoding of the status description, if it exists.

            byte[] buffer;
            int count = 2;
            if (string.IsNullOrEmpty(closeStatusDescription))
            {
                buffer = new byte[count];
            }
            else
            {
                count += s_textEncoding.GetByteCount(closeStatusDescription);
                buffer = new byte[count];
                int encodedLength = s_textEncoding.GetBytes(closeStatusDescription, 0, closeStatusDescription.Length, buffer, 2);
                Debug.Assert(count - 2 == encodedLength, @"GetByteCount and GetBytes encoded count didn't match");
            }

            ushort closeStatusValue = (ushort)closeStatus;
            buffer[0] = (byte)(closeStatusValue >> 8);
            buffer[1] = (byte)(closeStatusValue & 0xFF);

            return SendFrameAsync(MessageOpcode.Close, true, new ArraySegment<byte>(buffer, 0, count), cancellationToken)
                .ContinueWith(t =>
                {
                    if (buffer != null)
                    {
                        buffer = null;
                    }

                    lock (StateUpdateLock)
                    {
                        _sentCloseFrame = true;
                        if (State <= WebSocketState.CloseReceived)
                        {
                            State = WebSocketState.CloseSent;
                        }
                    }
                }, cancellationToken);

        }

        private void ConsumeFromBuffer(int count)
        {
            Debug.Assert(count >= 0, $"Expected non-negative count, got {count}");
            Debug.Assert(count <= _receiveBufferCount, $"Trying to consume {count}, which is more than exists {_receiveBufferCount}");
            _receiveBufferCount -= count;
            _receiveBufferOffset += count;
        }

        private Task EnsureBufferContainsAsync(int minimumRequiredBytes, CancellationToken cancellationToken, bool throwOnPrematureClosure = true)
        {
            Debug.Assert(minimumRequiredBytes <= _receiveBuffer.Length, $"Requested number of bytes {minimumRequiredBytes} must not exceed {_receiveBuffer.Length}");

            // If we don't have enough data in the buffer to satisfy the minimum required, read some more.
            if (_receiveBufferCount >= minimumRequiredBytes) return TaskEx.TaskCompleted;

            // If there's any data in the buffer, shift it down.  
            if (_receiveBufferCount > 0)
            {
                Buffer.BlockCopy(_receiveBuffer, _receiveBufferOffset, _receiveBuffer, 0, _receiveBufferCount);
            }
            _receiveBufferOffset = 0;

            return TaskEx.AsyncLoopTask(() =>
            {
                if (_receiveBufferCount >= minimumRequiredBytes) return TaskEx.TaskBreak;

                return _stream.ReadAsync(_receiveBuffer, _receiveBufferCount, _receiveBuffer.Length - _receiveBufferCount, cancellationToken)
                    .ContinueWith(t1 =>
                    {
                        int numRead = t1.Result;

                        Debug.Assert(numRead >= 0, $"Expected non-negative bytes read, got {numRead}");
                        _receiveBufferCount += numRead;
                        if (numRead == 0)
                        {
                            // The connection closed before we were able to read everything we needed.
                            // If it was due to use being disposed, fail.  If it was due to the connection
                            // being closed and it wasn't expected, fail.  If it was due to the connection
                            // being closed and that was expected, exit gracefully.
                            if (_disposed)
                            {
                                throw new ObjectDisposedException("ManagedWebSocket");
                            }
                            if (throwOnPrematureClosure)
                            {
                                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                            }
                            return TaskEx.TaskBreak;
                        }
                        return TaskEx.TaskContinue;
                    }, cancellationToken).Unwrap();
            });
        }

        /// <summary>Gets a send buffer from the pool.</summary>
        private void AllocateSendBuffer(int minLength)
        {
            Debug.Assert(_sendBuffer == null); // would only fail if had some catastrophic error previously that prevented cleaning up
            _sendBuffer = ArrayPool<byte>.Shared.Rent(minLength);
        }

        /// <summary>Releases the send buffer to the pool.</summary>
        private void ReleaseSendBuffer()
        {
            byte[] old = _sendBuffer;
            if (old != null)
            {
                _sendBuffer = null;
                ArrayPool<byte>.Shared.Return(old);
            }
        }

        private static int CombineMaskBytes(byte[] buffer, int maskOffset) => BitConverter.ToInt32(buffer, maskOffset);

        /// <summary>Applies a mask to a portion of a byte array.</summary>
        /// <param name="toMask">The buffer to which the mask should be applied.</param>
        /// <param name="toMaskOffset">The offset into <paramref name="toMask"/> at which the mask should start to be applied.</param>
        /// <param name="mask">The array containing the mask to apply.</param>
        /// <param name="maskOffset">The offset into <paramref name="mask"/> of the mask to apply of length <see cref="MaskLength"/>.</param>
        /// <param name="maskOffsetIndex">The next position offset from <paramref name="maskOffset"/> of which by to apply next from the mask.</param>
        /// <param name="count">The number of bytes starting from <paramref name="toMaskOffset"/> to which the mask should be applied.</param>
        /// <returns>The updated maskOffsetOffset value.</returns>
        private static int ApplyMask(byte[] toMask, int toMaskOffset, byte[] mask, int maskOffset, int maskOffsetIndex, long count)
        {
            Debug.Assert(maskOffsetIndex < MaskLength, $"Unexpected {nameof(maskOffsetIndex)}: {maskOffsetIndex}");
            Debug.Assert(mask.Length >= MaskLength + maskOffset, $"Unexpected inputs: {mask.Length}, {maskOffset}");
            return ApplyMask(toMask, toMaskOffset, CombineMaskBytes(mask, maskOffset), maskOffsetIndex, count);
        }

        /// <summary>Applies a mask to a portion of a byte array.</summary>
        /// <param name="toMask">The buffer to which the mask should be applied.</param>
        /// <param name="toMaskOffset">The offset into <paramref name="toMask"/> at which the mask should start to be applied.</param>
        /// <param name="mask">The four-byte mask, stored as an Int32.</param>
        /// <param name="maskIndex">The index into the mask.</param>
        /// <param name="count">The number of bytes to mask.</param>
        /// <returns>The next index into the mask to be used for future applications of the mask.</returns>
        private static unsafe int ApplyMask(byte[] toMask, int toMaskOffset, int mask, int maskIndex, long count)
        {
            int maskShift = maskIndex * 8;
            int shiftedMask = (int)(((uint)mask >> maskShift) | ((uint)mask << (32 - maskShift)));

            // If there are any bytes remaining (either we couldn't use vectors, or the count wasn't
            // an even multiple of the vector width), process them without vectors.
            if (count > 0)
            {
                fixed (byte* toMaskPtr = toMask)
                {
                    // Get the location in the target array to continue processing.
                    byte* p = toMaskPtr + toMaskOffset;

                    // Try to go an int at a time if the remaining data is 4-byte aligned and there's enough remaining.
                    if (((long)p % sizeof(int)) == 0)
                    {
                        while (count >= sizeof(int))
                        {
                            count -= sizeof(int);
                            *((int*)p) ^= shiftedMask;
                            p += sizeof(int);
                        }

                        // We don't need to update the maskIndex, as its mod-4 value won't have changed.
                        // `p` points to the remainder.
                    }

                    // Process any remaining data a byte at a time.
                    if (count > 0)
                    {
                        byte* maskPtr = (byte*)&mask;
                        byte* end = p + count;
                        while (p < end)
                        {
                            *p++ ^= maskPtr[maskIndex];
                            maskIndex = (maskIndex + 1) & 3;
                        }
                    }
                }
            }

            // Return the updated index.
            return maskIndex;
        }

        /// <summary>Aborts the websocket and throws an exception if an existing operation is in progress.</summary>
        private void ThrowIfOperationInProgress(Task operationTask, string methodName)
        {
            if (operationTask != null && !operationTask.IsCompleted)
            {
                Abort();
                throw new InvalidOperationException(SR.Format(SR.net_Websockets_AlreadyOneOutstandingOperation, methodName));
            }
        }

        /// <summary>Creates an OperationCanceledException instance, using a default message and the specified inner exception and token.</summary>
        private static Exception CreateOperationCanceledException(Exception innerException, CancellationToken cancellationToken = default(CancellationToken))
        {
            return new OperationCanceledException(new OperationCanceledException().Message, innerException);
        }

        // From https://raw.githubusercontent.com/aspnet/WebSockets/dev/src/Microsoft.AspNetCore.WebSockets.Protocol/Utilities.cs
        // Performs a stateful validation of UTF-8 bytes.
        // It checks for valid formatting, overlong encodings, surrogates, and value ranges.
        private static bool TryValidateUtf8(ArraySegment<byte> arraySegment, bool endOfMessage, Utf8MessageState state)
        {
            for (int i = arraySegment.Offset; i < arraySegment.Offset + arraySegment.Count;)
            {
                // Have we started a character sequence yet?
                if (!state.SequenceInProgress)
                {
                    // The first byte tells us how many bytes are in the sequence.
                    state.SequenceInProgress = true;
                    byte b = arraySegment.Array[i];
                    i++;
                    if ((b & 0x80) == 0) // 0bbbbbbb, single byte
                    {
                        state.AdditionalBytesExpected = 0;
                        state.CurrentDecodeBits = b & 0x7F;
                        state.ExpectedValueMin = 0;
                    }
                    else if ((b & 0xC0) == 0x80)
                    {
                        // Misplaced 10bbbbbb continuation byte. This cannot be the first byte.
                        return false;
                    }
                    else if ((b & 0xE0) == 0xC0) // 110bbbbb 10bbbbbb
                    {
                        state.AdditionalBytesExpected = 1;
                        state.CurrentDecodeBits = b & 0x1F;
                        state.ExpectedValueMin = 0x80;
                    }
                    else if ((b & 0xF0) == 0xE0) // 1110bbbb 10bbbbbb 10bbbbbb
                    {
                        state.AdditionalBytesExpected = 2;
                        state.CurrentDecodeBits = b & 0xF;
                        state.ExpectedValueMin = 0x800;
                    }
                    else if ((b & 0xF8) == 0xF0) // 11110bbb 10bbbbbb 10bbbbbb 10bbbbbb
                    {
                        state.AdditionalBytesExpected = 3;
                        state.CurrentDecodeBits = b & 0x7;
                        state.ExpectedValueMin = 0x10000;
                    }
                    else // 111110bb & 1111110b & 11111110 && 11111111 are not valid
                    {
                        return false;
                    }
                }
                while (state.AdditionalBytesExpected > 0 && i < arraySegment.Offset + arraySegment.Count)
                {
                    byte b = arraySegment.Array[i];
                    if ((b & 0xC0) != 0x80)
                    {
                        return false;
                    }

                    i++;
                    state.AdditionalBytesExpected--;

                    // Each continuation byte carries 6 bits of data 0x10bbbbbb.
                    state.CurrentDecodeBits = (state.CurrentDecodeBits << 6) | (b & 0x3F);

                    if (state.AdditionalBytesExpected == 1 && state.CurrentDecodeBits >= 0x360 && state.CurrentDecodeBits <= 0x37F)
                    {
                        // This is going to end up in the range of 0xD800-0xDFFF UTF-16 surrogates that are not allowed in UTF-8;
                        return false;
                    }
                    if (state.AdditionalBytesExpected == 2 && state.CurrentDecodeBits >= 0x110)
                    {
                        // This is going to be out of the upper Unicode bound 0x10FFFF.
                        return false;
                    }
                }
                if (state.AdditionalBytesExpected == 0)
                {
                    state.SequenceInProgress = false;
                    if (state.CurrentDecodeBits < state.ExpectedValueMin)
                    {
                        // Overlong encoding (e.g. using 2 bytes to encode something that only needed 1).
                        return false;
                    }
                }
            }
            if (endOfMessage && state.SequenceInProgress)
            {
                return false;
            }
            return true;
        }

        private sealed class Utf8MessageState
        {
            internal bool SequenceInProgress;
            internal int AdditionalBytesExpected;
            internal int ExpectedValueMin;
            internal int CurrentDecodeBits;
        }

        private enum MessageOpcode : byte
        {
            Continuation = 0x0,
            Text = 0x1,
            Binary = 0x2,
            Close = 0x8,
            Ping = 0x9,
            Pong = 0xA
        }

        [StructLayout(LayoutKind.Auto)]
        private struct MessageHeader
        {
            internal MessageOpcode Opcode;
            internal bool Fin;
            internal long PayloadLength;
            internal int Mask;
        }
    }
}