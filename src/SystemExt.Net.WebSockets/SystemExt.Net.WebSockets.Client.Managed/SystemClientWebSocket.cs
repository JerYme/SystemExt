using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace System.Net.WebSockets.Client
{
   public static class SystemClientWebSocket
   {
      /// <summary>
      /// False if System.Net.WebSockets.ClientWebSocket is available on this platform, true if System.Net.WebSockets.ClientWebSocket is required.
      /// </summary>
      public static bool ManagedWebSocketRequired => _managedWebSocketRequired.Value;

      static Lazy<bool> _managedWebSocketRequired => new Lazy<bool>(CheckManagedWebSocketRequired);

      static bool CheckManagedWebSocketRequired()
      {
         try
         {
            using (var clientWebSocket = new WebSockets.ClientWebSocket())
            {
               return false;
            }
         }
         catch (PlatformNotSupportedException)
         {
            return true;
         }
      }

      /// <summary>
      /// Creates a ClientWebSocket that works for this platform. Uses System.Net.WebSockets.ClientWebSocket if supported or System.Net.WebSockets.ClientWebSocket if not.
      /// </summary>
      public static WebSocket CreateClientWebSocket()
      {
         if (ManagedWebSocketRequired)
         {
            return new Client.ClientWebSocket();
         }
         else
         {
            return new WebSockets.ClientWebSocket();
         }
      }

      /// <summary>
      /// Creates and connects a ClientWebSocket that works for this platform. Uses System.Net.WebSockets.ClientWebSocket if supported or System.Net.WebSockets.ClientWebSocket if not.
      /// </summary>
      public static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
      {
         var clientWebSocket = CreateClientWebSocket();
         await clientWebSocket.ConnectAsync(uri, cancellationToken);
         return clientWebSocket;
      }

      public static Task ConnectAsync(this WebSocket clientWebSocket, Uri uri, CancellationToken cancellationToken)
      {
         if (clientWebSocket is WebSockets.ClientWebSocket)
         {
            return (clientWebSocket as WebSockets.ClientWebSocket).ConnectAsync(uri, cancellationToken);
         }
         else if (clientWebSocket is Client.ClientWebSocket)
         {
            return (clientWebSocket as Client.ClientWebSocket).ConnectAsync(uri, cancellationToken);
         }

         throw new ArgumentException(@"WebSocket must be an instance of System.Net.WebSockets.ClientWebSocket or System.Net.WebSockets.ClientWebSocket", nameof(clientWebSocket));
      }

   }
}
