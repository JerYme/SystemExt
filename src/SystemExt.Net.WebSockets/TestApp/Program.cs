using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClientWebSocket = System.Net.WebSockets.Client.ClientWebSocket;

namespace TestApp
{
    class Program
    {
        const string WS_TEST_SERVER = "ws://echo.websocket.org";
        const string WSS_TEST_SERVER = "wss://echo.websocket.org";

        static void Main(string[] args)
        {
            TestConnection(WS_TEST_SERVER).GetAwaiter().GetResult();
            TestConnection(WSS_TEST_SERVER).GetAwaiter().GetResult();
        }

        static async Task TestConnection(string server)
        {
            var proxy = WebRequest.DefaultWebProxy;
            var credentials = CredentialCache.DefaultCredentials;
            proxy.Credentials = credentials;
            WebRequest.DefaultWebProxy = proxy;

            using (var ws = new ClientWebSocket())
            {
                var uri = new Uri(server);
                await ws.ConnectAsync(uri, CancellationToken.None);

                var buffer = new ArraySegment<byte>(new byte[1024 * 4]);
                var readTask = ws.ReceiveAsync(buffer, CancellationToken.None);

                const string msg = "hello";
                var testMsg = new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg));
                await ws.SendAsync(testMsg, WebSocketMessageType.Text, true, CancellationToken.None);

                var read = await readTask;
                var reply = Encoding.UTF8.GetString(buffer.Array, 0, read.Count);

                if (reply != msg)
                {
                    throw new Exception($"Expected to read back '{msg}' but got '{reply}' for server {server}");
                }
                Console.WriteLine("Success connecting to server " + server);
                Console.WriteLine("Press enter to exit ");
                Console.ReadLine();
            }
        }
    }
}
