using System.Net;

namespace Parrot.Web;

/// <summary>Determines which host names and resolved IP addresses are eligible for web connections.</summary>
internal interface IWebAddressPolicy
{
    bool AllowsHost(string host);

    bool Allows(IPAddress address);
}
