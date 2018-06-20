namespace System
{
    static class UriExtensions
    {
        public static string GetIdnHost(this Uri uri) => new Globalization.IdnMapping().GetAscii(uri.Host);
    }
}