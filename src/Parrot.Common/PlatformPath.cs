namespace Parrot;

internal static class PlatformPath
{
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

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
