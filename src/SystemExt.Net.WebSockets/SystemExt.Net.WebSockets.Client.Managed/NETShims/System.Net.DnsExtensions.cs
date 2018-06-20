using System.Linq;
using System.Threading.Tasks;

namespace System.Net
{
    public static class DnsEx
    {
        public static Task<IPAddress[]> GetHostAddressesAsync(string hostNameOrAddress)
        {
            try
            {
                var proxy = WebRequest.DefaultWebProxy;
                var credentials = CredentialCache.DefaultCredentials;

                var request = WebRequest.CreateHttp("http://" + hostNameOrAddress);
                request.UseDefaultCredentials = true;
                WebRequest.DefaultWebProxy = proxy;
                request.Proxy = proxy;
                request.Proxy.Credentials = credentials;
                request.Credentials = credentials;
                request.Method = "GET";
                request.AllowAutoRedirect = false;

                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    //some code here
                }
            }
            catch (Exception e)
            {
                //Some other code here
            }

            var x2 = Dns.GetHostAddresses(hostNameOrAddress);

            return Task<IPAddress[]>.Factory.FromAsync(Dns.BeginGetHostAddresses, Dns.EndGetHostAddresses, hostNameOrAddress, null);
        }

    }
}