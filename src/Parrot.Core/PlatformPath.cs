namespace Parrot;

internal static class PlatformPath
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsMacOS())
        {
            return full;
        }

        return full switch
        {
            "/var" => "/private/var",
            "/tmp" => "/private/tmp",
            "/etc" => "/private/etc",
            _ when full.StartsWith("/var/", StringComparison.Ordinal) => "/private" + full,
            _ when full.StartsWith("/tmp/", StringComparison.Ordinal) => "/private" + full,
            _ when full.StartsWith("/etc/", StringComparison.Ordinal) => "/private" + full,
            _ => full,
        };
    }
}
