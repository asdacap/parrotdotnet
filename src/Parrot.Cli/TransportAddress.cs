using System.Net;

namespace Parrot.Cli;

internal sealed record TransportAddress(TransportAddressKind Kind, string Value)
{
    public bool IsTcp => Kind is TransportAddressKind.Http or TransportAddressKind.Https;

    public bool IsExternal => IsTcp && !IsLoopback(new Uri(Value, UriKind.Absolute));

    public string UnixPath => Kind == TransportAddressKind.Unix
        ? Value
        : throw new InvalidOperationException("The transport is not a Unix socket.");

    public Uri TcpUri => IsTcp
        ? new Uri(Value, UriKind.Absolute)
        : throw new InvalidOperationException("The transport is not TCP.");

    public static TransportAddress Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.StartsWith("unix:", StringComparison.Ordinal))
        {
            var path = value["unix:".Length..];
            if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException("a unix transport needs an absolute path: unix:/path");
            }

            return new(TransportAddressKind.Unix, Path.GetFullPath(path));
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException("transport must be unix:/path, http://host:port, or https://host:port");
        }

        if (uri.AbsolutePath is not ("" or "/"))
        {
            throw new InvalidOperationException("a TCP transport address cannot contain a path");
        }

        return new(
            uri.Scheme == Uri.UriSchemeHttps ? TransportAddressKind.Https : TransportAddressKind.Http,
            uri.GetLeftPart(UriPartial.Authority));
    }

    private static bool IsLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}
