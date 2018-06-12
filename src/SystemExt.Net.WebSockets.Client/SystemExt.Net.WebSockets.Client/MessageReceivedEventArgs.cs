using System.Threading;

namespace System.Net.WebSockets
{
    /// <summary> Provides additional data for the <see cref="ClientWebSocketText.MessageReceived"/> event.</summary>
    public sealed class MessageReceivedEventArgs : EventArgs
    {
        /// <summary>Gets or sets the message that was received.</summary>
        public string Message { get; set; }

        public CancellationToken CancellationToken { get; set; }
    }
}