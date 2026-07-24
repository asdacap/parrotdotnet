using System.Net;

namespace Parrot.Web;

internal interface IWebAddressPolicy
{
    bool AllowsHost(string host);

    bool Allows(IPAddress address);
}
