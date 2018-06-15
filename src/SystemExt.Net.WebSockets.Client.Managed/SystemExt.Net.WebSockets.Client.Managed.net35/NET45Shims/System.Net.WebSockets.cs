using System.ComponentModel;
using System.IO;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security;
using System.Security.Permissions;
using System.Threading;


// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using System.Threading;

using WebSocketBase = System.Net.WebSockets.Managed.ManagedWebSocket;
using WebSocket = System.Net.WebSockets.Managed.ManagedWebSocket;

namespace System.Net.WebSockets
{
  /// <summary>Defines the different states a WebSockets instance can be in.</summary>
  public enum WebSocketState
  {
    None,
    Connecting,
    Open,
    CloseSent,
    CloseReceived,
    Closed,
    Aborted,
  }

  /// <summary>Represents well known WebSocket close codes as defined in section 11.7 of the WebSocket protocol spec.</summary>
  public enum WebSocketCloseStatus
  {
    NormalClosure = 1000,
    EndpointUnavailable = 1001,
    ProtocolError = 1002,
    InvalidMessageType = 1003,
    Empty = 1005,
    InvalidPayloadData = 1007,
    PolicyViolation = 1008,
    MessageTooBig = 1009,
    MandatoryExtension = 1010,
    InternalServerError = 1011,
  }

  /// <summary>Indicates the message type.</summary>
  public enum WebSocketMessageType
  {
    Text,
    Binary,
    Close,
  }

  public enum WebSocketError
  {
    Success,
    InvalidMessageType,
    Faulted,
    NativeError,
    NotAWebSocket,
    UnsupportedVersion,
    UnsupportedProtocol,
    HeaderError,
    ConnectionClosedPrematurely,
    InvalidState,
  }



  /// <summary>An instance of this class represents the result of performing a single ReceiveAsync operation on a WebSocket.</summary>
  public class WebSocketReceiveResult
  {
    /// <summary>Indicates the number of bytes that the WebSocket received.</summary>
    /// <returns>Returns <see cref="T:System.Int32" />.</returns>
    public int Count { get; private set; }

    /// <summary>Indicates whether the message has been received completely.</summary>
    /// <returns>Returns <see cref="T:System.Boolean" />.</returns>
    public bool EndOfMessage { get; private set; }

    /// <summary>Indicates whether the current message is a UTF-8 message or a binary message.</summary>
    /// <returns>Returns <see cref="T:System.Net.WebSockets.WebSocketMessageType" />.</returns>
    public WebSocketMessageType MessageType { get; private set; }

    /// <summary>Indicates the reason why the remote endpoint initiated the close handshake.</summary>
    /// <returns>Returns <see cref="T:System.Net.WebSockets.WebSocketCloseStatus" />.</returns>
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    /// <summary>Returns the optional description that describes why the close handshake has been initiated by the remote endpoint.</summary>
    /// <returns>Returns <see cref="T:System.String" />.</returns>
    public string CloseStatusDescription { get; private set; }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketReceiveResult" /> class.</summary>
    /// <param name="count">The number of bytes received.</param>
    /// <param name="messageType">The type of message that was received.</param>
    /// <param name="endOfMessage">Indicates whether this is the final message.</param>
    public WebSocketReceiveResult(int count, WebSocketMessageType messageType, bool endOfMessage)
      : this(count, messageType, endOfMessage, new WebSocketCloseStatus?(), (string)null)
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketReceiveResult" /> class.</summary>
    /// <param name="count">The number of bytes received.</param>
    /// <param name="messageType">The type of message that was received.</param>
    /// <param name="endOfMessage">Indicates whether this is the final message.</param>
    /// <param name="closeStatus">Indicates the <see cref="T:System.Net.WebSockets.WebSocketCloseStatus" /> of the connection.</param>
    /// <param name="closeStatusDescription">The description of <paramref name="closeStatus" />.</param>
    public WebSocketReceiveResult(int count, WebSocketMessageType messageType, bool endOfMessage, WebSocketCloseStatus? closeStatus, string closeStatusDescription)
    {
      if (count < 0)
        throw new ArgumentOutOfRangeException("count");
      this.Count = count;
      this.EndOfMessage = endOfMessage;
      this.MessageType = messageType;
      this.CloseStatus = closeStatus;
      this.CloseStatusDescription = closeStatusDescription;
    }

    internal WebSocketReceiveResult Copy(int count)
    {
      this.Count = this.Count - count;
      return new WebSocketReceiveResult(count, this.MessageType, this.Count == 0 && this.EndOfMessage, this.CloseStatus, this.CloseStatusDescription);
    }
  }

  internal static class WebSocketProtocolComponent
  {
    private static readonly string s_DummyWebsocketKeyBase64 = Convert.ToBase64String(new byte[16]);
    private static readonly WebSocketProtocolComponent.HttpHeader[] s_InitialClientRequestHeaders = new WebSocketProtocolComponent.HttpHeader[2]
    {
      new WebSocketProtocolComponent.HttpHeader()
      {
        Name = "Connection",
        NameLength = (uint) "Connection".Length,
        Value = "Upgrade",
        ValueLength = (uint) "Upgrade".Length
      },
      new WebSocketProtocolComponent.HttpHeader()
      {
        Name = "Upgrade",
        NameLength = (uint) "Upgrade".Length,
        Value = "websocket",
        ValueLength = (uint) "websocket".Length
      }
    };
    private static readonly string s_SupportedVersion;
    private static readonly WebSocketProtocolComponent.HttpHeader[] s_ServerFakeRequestHeaders;



    [SecuritySafeCritical]
    [FileIOPermission(SecurityAction.Assert, AllFiles = FileIOPermissionAccess.PathDiscovery)]
    static WebSocketProtocolComponent()
    {
      WebSocketProtocolComponent.HttpHeader[] httpHeaderArray = new WebSocketProtocolComponent.HttpHeader[5];
      httpHeaderArray[0] = new WebSocketProtocolComponent.HttpHeader()
      {
        Name = "Connection",
        NameLength = (uint)"Connection".Length,
        Value = "Upgrade",
        ValueLength = (uint)"Upgrade".Length
      };
      httpHeaderArray[1] = new WebSocketProtocolComponent.HttpHeader()
      {
        Name = "Upgrade",
        NameLength = (uint)"Upgrade".Length,
        Value = "websocket",
        ValueLength = (uint)"websocket".Length
      };
      int index1 = 2;
      WebSocketProtocolComponent.HttpHeader httpHeader1 = new WebSocketProtocolComponent.HttpHeader();
      httpHeader1.Name = "Host";
      httpHeader1.NameLength = (uint)"Host".Length;
      httpHeader1.Value = string.Empty;
      httpHeader1.ValueLength = 0U;
      WebSocketProtocolComponent.HttpHeader httpHeader2 = httpHeader1;
      httpHeaderArray[index1] = httpHeader2;
      int index2 = 3;
      httpHeader1 = new WebSocketProtocolComponent.HttpHeader();
      httpHeader1.Name = "Sec-WebSocket-Version";
      httpHeader1.NameLength = (uint)"Sec-WebSocket-Version".Length;
      httpHeader1.Value = WebSocketProtocolComponent.s_SupportedVersion;
      httpHeader1.ValueLength = (uint)WebSocketProtocolComponent.s_SupportedVersion.Length;
      WebSocketProtocolComponent.HttpHeader httpHeader3 = httpHeader1;
      httpHeaderArray[index2] = httpHeader3;
      int index3 = 4;
      httpHeader1 = new WebSocketProtocolComponent.HttpHeader();
      httpHeader1.Name = "Sec-WebSocket-Key";
      httpHeader1.NameLength = (uint)"Sec-WebSocket-Key".Length;
      httpHeader1.Value = WebSocketProtocolComponent.s_DummyWebsocketKeyBase64;
      httpHeader1.ValueLength = (uint)WebSocketProtocolComponent.s_DummyWebsocketKeyBase64.Length;
      WebSocketProtocolComponent.HttpHeader httpHeader4 = httpHeader1;
      httpHeaderArray[index3] = httpHeader4;
      WebSocketProtocolComponent.s_ServerFakeRequestHeaders = httpHeaderArray;
    }





    public static bool Succeeded(int hr)
    {
      return hr >= 0;
    }

    internal static class Errors
    {
      internal const int E_INVALID_OPERATION = -2147483568;
      internal const int E_INVALID_PROTOCOL_OPERATION = -2147483567;
      internal const int E_INVALID_PROTOCOL_FORMAT = -2147483566;
      internal const int E_NUMERIC_OVERFLOW = -2147483565;
      internal const int E_FAIL = -2147467259;
    }

    internal enum Action
    {
      NoAction,
      SendToNetwork,
      IndicateSendComplete,
      ReceiveFromNetwork,
      IndicateReceiveComplete,
    }

    internal enum BufferType : uint
    {
      None = 0,
      UTF8Message = 2147483648,
      UTF8Fragment = 2147483649,
      BinaryMessage = 2147483650,
      BinaryFragment = 2147483651,
      Close = 2147483652,
      PingPong = 2147483653,
      UnsolicitedPong = 2147483654,
    }

    internal enum PropertyType
    {
      ReceiveBufferSize,
      SendBufferSize,
      DisableMasking,
      AllocatedBuffer,
      DisableUtf8Verification,
      KeepAliveInterval,
    }

    internal enum ActionQueue
    {
      Send = 1,
      Receive = 2,
    }

    internal struct Property
    {
      internal WebSocketProtocolComponent.PropertyType Type;
      internal IntPtr PropertyData;
      internal uint PropertySize;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct Buffer
    {
      [FieldOffset(0)]
      internal WebSocketProtocolComponent.DataBuffer Data;
      [FieldOffset(0)]
      internal WebSocketProtocolComponent.CloseBuffer CloseStatus;
    }

    internal struct DataBuffer
    {
      internal IntPtr BufferData;
      internal uint BufferLength;
    }

    internal struct CloseBuffer
    {
      internal IntPtr ReasonData;
      internal uint ReasonLength;
      internal ushort CloseStatus;
    }

    internal struct HttpHeader
    {
      [MarshalAs(UnmanagedType.LPStr)]
      internal string Name;
      internal uint NameLength;
      [MarshalAs(UnmanagedType.LPStr)]
      internal string Value;
      internal uint ValueLength;
    }
  }


  /// <summary>Represents an exception that occurred when performing an operation on a WebSocket connection.</summary>
  [Serializable]
  public sealed class WebSocketException : Win32Exception
  {
    private WebSocketError m_WebSocketErrorCode;

    /// <summary>The native error code for the exception that occurred.</summary>
    /// <returns>Returns <see cref="T:System.Int32" />.</returns>
    public override int ErrorCode
    {
      get
      {
        return this.NativeErrorCode;
      }
    }

    /// <summary>Returns a WebSocketError indicating the type of error that occurred.</summary>
    /// <returns>Returns <see cref="T:System.Net.WebSockets.WebSocketError" />.</returns>
    public WebSocketError WebSocketErrorCode
    {
      get
      {
        return this.m_WebSocketErrorCode;
      }
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    public WebSocketException()
      : this(Marshal.GetLastWin32Error())
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    public WebSocketException(WebSocketError error)
      : this(error, WebSocketException.GetErrorMessage(error))
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="message">The description of the error.</param>
    public WebSocketException(WebSocketError error, string message)
      : base(message)
    {
      this.m_WebSocketErrorCode = error;
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="innerException">Indicates the previous exception that led to the current exception.</param>
    public WebSocketException(WebSocketError error, Exception innerException)
      : this(error, GetErrorMessage(error), innerException)
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="message">The description of the error.</param>
    /// <param name="innerException">Indicates the previous exception that led to the current exception.</param>
    public WebSocketException(WebSocketError error, string message, Exception innerException)
      : base(message, innerException)
    {
      this.m_WebSocketErrorCode = error;
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="nativeError">The native error code for the exception.</param>
    public WebSocketException(int nativeError)
      : base(nativeError)
    {
      this.m_WebSocketErrorCode = !WebSocketProtocolComponent.Succeeded(nativeError) ? WebSocketError.NativeError : WebSocketError.Success;
      this.SetErrorCodeOnError(nativeError);
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="nativeError">The native error code for the exception.</param>
    /// <param name="message">The description of the error.</param>
    public WebSocketException(int nativeError, string message)
      : base(nativeError, message)
    {
      this.m_WebSocketErrorCode = !WebSocketProtocolComponent.Succeeded(nativeError) ? WebSocketError.NativeError : WebSocketError.Success;
      this.SetErrorCodeOnError(nativeError);
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="nativeError">The native error code for the exception.</param>
    /// <param name="innerException">Indicates the previous exception that led to the current exception.</param>
    public WebSocketException(int nativeError, Exception innerException)
      : base(SR.GetString("net_WebSockets_Generic"), innerException)
    {
      this.m_WebSocketErrorCode = !WebSocketProtocolComponent.Succeeded(nativeError) ? WebSocketError.NativeError : WebSocketError.Success;
      this.SetErrorCodeOnError(nativeError);
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="nativeError">The native error code for the exception.</param>
    public WebSocketException(WebSocketError error, int nativeError)
      : this(error, nativeError, WebSocketException.GetErrorMessage(error))
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="nativeError">The native error code for the exception.</param>
    /// <param name="message">The description of the error.</param>
    public WebSocketException(WebSocketError error, int nativeError, string message)
      : base(message)
    {
      this.m_WebSocketErrorCode = error;
      this.SetErrorCodeOnError(nativeError);
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="nativeError">The native error code for the exception.</param>
    /// <param name="innerException">Indicates the previous exception that led to the current exception.</param>
    public WebSocketException(WebSocketError error, int nativeError, Exception innerException)
      : this(error, nativeError, WebSocketException.GetErrorMessage(error), innerException)
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="error">The error from the WebSocketError enumeration.</param>
    /// <param name="nativeError">The native error code for the exception.</param>
    /// <param name="message">The description of the error.</param>
    /// <param name="innerException">Indicates the previous exception that led to the current exception.</param>
    public WebSocketException(WebSocketError error, int nativeError, string message, Exception innerException)
      : base(message, innerException)
    {
      this.m_WebSocketErrorCode = error;
      this.SetErrorCodeOnError(nativeError);
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="message">The description of the error.</param>
    public WebSocketException(string message)
      : base(message)
    {
    }

    /// <summary>Creates an instance of the <see cref="T:System.Net.WebSockets.WebSocketException" /> class.</summary>
    /// <param name="message">The description of the error.</param>
    /// <param name="innerException">Indicates the previous exception that led to the current exception.</param>
    public WebSocketException(string message, Exception innerException)
      : base(message, innerException)
    {
    }

    private WebSocketException(SerializationInfo serializationInfo, StreamingContext streamingContext)
      : base(serializationInfo, streamingContext)
    {
    }

    private static string GetErrorMessage(WebSocketError error)
    {
      switch (error)
      {
        case WebSocketError.InvalidMessageType:
          return SR.GetString("net_WebSockets_InvalidMessageType_Generic", (object)(typeof(WebSocket).Name + "CloseAsync"), (object)(typeof(WebSocket).Name + "CloseOutputAsync"));
        case WebSocketError.Faulted:
          return SR.GetString("net_Websockets_WebSocketBaseFaulted");
        case WebSocketError.NotAWebSocket:
          return SR.GetString("net_WebSockets_NotAWebSocket_Generic");
        case WebSocketError.UnsupportedVersion:
          return SR.GetString("net_WebSockets_UnsupportedWebSocketVersion_Generic");
        case WebSocketError.UnsupportedProtocol:
          return SR.GetString("net_WebSockets_UnsupportedProtocol_Generic");
        case WebSocketError.HeaderError:
          return SR.GetString("net_WebSockets_HeaderError_Generic");
        case WebSocketError.ConnectionClosedPrematurely:
          return SR.GetString("net_WebSockets_ConnectionClosedPrematurely_Generic");
        case WebSocketError.InvalidState:
          return SR.GetString("net_WebSockets_InvalidState_Generic");
        default:
          return SR.GetString("net_WebSockets_Generic");
      }
    }

    /// <summary>Sets the SerializationInfo object with the file name and line number where the exception occurred.</summary>
    /// <param name="info">A SerializationInfo object.</param>
    /// <param name="context">The contextual information about the source or destination.</param>
    [SecurityPermission(SecurityAction.Demand, SerializationFormatter = true)]
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
      if (info == null)
        throw new ArgumentNullException("info");
      info.AddValue("WebSocketErrorCode", (object)this.m_WebSocketErrorCode);
      base.GetObjectData(info, context);
    }

    private void SetErrorCodeOnError(int nativeError)
    {
      if (WebSocketProtocolComponent.Succeeded(nativeError))
        return;
      this.HResult = nativeError;
    }
  }



}

