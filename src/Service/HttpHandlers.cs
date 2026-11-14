using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Google.Apis.Http;

namespace Service
{
    public static class HttpHandlers
    {
        /// <summary>Creates a handler; when <paramref name="ipv4Only"/> is set all connections use IPv4. An optional http/socks5 proxy can be given.</summary>
        public static SocketsHttpHandler Create(bool ipv4Only, string proxyUrl = null)
        {
            var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
            if (!string.IsNullOrWhiteSpace(proxyUrl))
            {
                handler.Proxy = new WebProxy(new System.Uri(proxyUrl));
                handler.UseProxy = true;
            }
            if (ipv4Only)
            {
                handler.ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(
                            new DnsEndPoint(context.DnsEndPoint.Host, context.DnsEndPoint.Port, AddressFamily.InterNetwork),
                            cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                };
            }

            return handler;
        }
    }

    public sealed class GoogleHttpClientFactory : HttpClientFactory
    {
        private readonly bool _ipv4Only;

        public GoogleHttpClientFactory(bool ipv4Only) => _ipv4Only = ipv4Only;

        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
            HttpHandlers.Create(_ipv4Only);
    }
}
