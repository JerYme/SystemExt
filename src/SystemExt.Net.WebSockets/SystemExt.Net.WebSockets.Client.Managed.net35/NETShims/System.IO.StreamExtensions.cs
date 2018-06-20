using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Permissions;
using System.Threading;
using System.Threading.Tasks;

namespace System.IO
{
    static class StreamExtensions
    {
        public static Task WriteAsync(this Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.Factory.FromAsync(
                (targetBuffer, targetOffet, targetCount, callback, state) => ((Stream)state).BeginWrite(targetBuffer, targetOffet, targetCount, callback, state),
                asyncResult => ((Stream)asyncResult.AsyncState).EndWrite(asyncResult),
                buffer, offset, count,
                stream);
        }

        public static Task<int> ReadAsync(this Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.Factory.FromAsync(
                (targetBuffer, targetOffet, targetCount, callback, state) => ((Stream)state).BeginRead(targetBuffer, targetOffet, targetCount, callback, state),
                asyncResult => ((Stream)asyncResult.AsyncState).EndRead(asyncResult),
                buffer, offset, count,
                stream);
        }

        [HostProtection(SecurityAction.LinkDemand, ExternalThreading = true)]
        public static Task AuthenticateAsClientAsync(this SslStream stream, string targetHost, X509CertificateCollection clientCertificates, SslProtocols enabledSslProtocols, bool checkCertificateRevocation)
        {
            return Task.Factory.FromAsync(
                (callback, state) => stream.BeginAuthenticateAsClient(targetHost, clientCertificates, enabledSslProtocols, checkCertificateRevocation, callback, state),
                stream.EndAuthenticateAsClient,
                null);
        }

    }
}