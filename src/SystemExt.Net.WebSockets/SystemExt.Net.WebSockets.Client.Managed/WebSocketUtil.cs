using System.IO;
using System.Net.WebSockets.Client;
using System.Threading;

namespace System
{
    static class WebSocketUtil
    {
        public static ManagedWebSocket CreateClientWebSocket(Stream innerStream,
            string subProtocol, int receiveBufferSize, int sendBufferSize,
            TimeSpan keepAliveInterval, bool useZeroMaskingKey, ArraySegment<byte> internalBuffer)
        {
            if (innerStream == null)
            {
                throw new ArgumentNullException(nameof(innerStream));
            }

            if (!innerStream.CanRead || !innerStream.CanWrite)
            {
                throw new ArgumentException(!innerStream.CanRead ? SR.NotReadableStream : SR.NotWriteableStream, nameof(innerStream));
            }

            if (subProtocol != null)
            {
                WebSocketValidate.ValidateSubprotocol(subProtocol);
            }

            if (keepAliveInterval != Timeout.InfiniteTimeSpan && keepAliveInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(keepAliveInterval), keepAliveInterval,
                    SR.Format(SR.net_WebSockets_ArgumentOutOfRange_TooSmall,
                        0));
            }

            if (receiveBufferSize <= 0 || sendBufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    receiveBufferSize <= 0 ? nameof(receiveBufferSize) : nameof(sendBufferSize),
                    receiveBufferSize <= 0 ? receiveBufferSize : sendBufferSize,
                    SR.Format(SR.net_WebSockets_ArgumentOutOfRange_TooSmall, 0));
            }

            return ManagedWebSocket.CreateFromConnectedStream(
                innerStream, false, subProtocol, keepAliveInterval,
                receiveBufferSize, internalBuffer);
        }
    }
}