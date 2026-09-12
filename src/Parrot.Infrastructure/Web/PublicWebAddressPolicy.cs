using System.Net;
using System.Net.Sockets;

namespace Parrot.Web;

internal sealed class PublicWebAddressPolicy : IWebAddressPolicy
{
    private static readonly byte[][] ForbiddenIpv6Prefixes =
    [
        [0x00, 0x64, 0xff, 0x9b, 0x00, 0x01],
        [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x20, 0x01, 0x00, 0x02, 0x00, 0x00],
        [0x20, 0x01, 0x0d, 0xb8],
    ];

    public bool AllowsHost(string host) =>
        !host.Equals("localhost", StringComparison.Ordinal)
        && !host.EndsWith(".localhost", StringComparison.Ordinal);

    public bool Allows(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => AllowsIpv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => AllowsIpv6(address),
            _ => false,
        };
    }

    private static bool AllowsIpv4(byte[] address)
    {
        var first = address[0];
        var second = address[1];

        return first != 0
            && first != 10
            && first != 127
            && !(first == 100 && second is >= 64 and <= 127)
            && !(first == 169 && second == 254)
            && !(first == 172 && second is >= 16 and <= 31)
            && !(first == 192 && second == 0 && address[2] == 0)
            && !(first == 192 && second == 0 && address[2] == 2)
            && !(first == 192 && second == 168)
            && !(first == 198 && second is 18 or 19)
            && !(first == 198 && second == 51 && address[2] == 100)
            && !(first == 203 && second == 0 && address[2] == 113)
            && first < 224;
    }

    private static bool AllowsIpv6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Any)
            || IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();

        if ((bytes[0] & 0xfe) == 0xfc)
        {
            return false;
        }

        return !ForbiddenIpv6Prefixes.Any(prefix => bytes.AsSpan().StartsWith(prefix));
    }
}
