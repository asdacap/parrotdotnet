using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Parrot.Web;

internal static class WebConnection
{
    public static async ValueTask<Stream> Connect(
        ConcurrentDictionary<string, IPAddress[]> pins,
        DnsEndPoint endpoint,
        CancellationToken cancellationToken)
    {
        if (!pins.TryGetValue(WebFetchText.CanonicalHost(endpoint.Host), out var addresses))
        {
            throw new WebFetchException("web fetch attempted to connect to an unvalidated host");
        }

        Exception? lastFailure = null;

        foreach (var address in addresses)
        {
            Socket? socket = null;

            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                return stream;
            }
            catch (SocketException failure)
            {
                lastFailure = failure;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw new WebFetchException("web fetch could not connect to the validated host", lastFailure ?? new SocketException());
    }
}
