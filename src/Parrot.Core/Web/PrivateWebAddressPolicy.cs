using System.Net;
using System.Net.Sockets;

namespace Parrot.Web;

internal sealed class PrivateWebAddressPolicy : IWebAddressPolicy
{
    public bool AllowsHost(string host) => true;

    public bool Allows(IPAddress address) =>
        !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any)
        && !IsMulticast(address);

    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] is >= 224 and <= 239;
    }
}
