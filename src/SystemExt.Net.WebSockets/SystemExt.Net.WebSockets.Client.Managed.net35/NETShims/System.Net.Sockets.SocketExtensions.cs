using System.Threading.Tasks;

namespace System.Net.Sockets
{
    static class SocketExtensions
    {
        public static Task ConnectAsync(this Socket socket, IPAddress address, int port)
        {
            return Task.Factory.FromAsync(
                (targetAddress, targetPort, callback, state) => ((Socket) state).BeginConnect(targetAddress, targetPort, callback, state),
                asyncResult => ((Socket) asyncResult.AsyncState).EndConnect(asyncResult),
                address,
                port,
                state: socket);
        }
    }
}
