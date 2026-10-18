using System.Net;
using Xamarin.Android.Net;

namespace BunkrAlbum;

static class AndroidHttp
{
    public static HttpMessageHandler Create()
    {
        return new AndroidMessageHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
    }
}
