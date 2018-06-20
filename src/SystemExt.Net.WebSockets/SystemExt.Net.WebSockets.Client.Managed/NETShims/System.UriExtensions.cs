namespace System
{
    static class UriExtensions
    {
        public static string GetIdnHost(this Uri uri)
        {
            return new Globalization.IdnMapping().GetAscii(uri.Host);
        }
    }
}