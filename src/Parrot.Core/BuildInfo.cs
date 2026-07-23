using System.Reflection;

namespace Parrot;

public static class BuildInfo
{
    public const string ProductName = "parrot";

    public static string Version { get; } = ReadInformationalVersion();

    private static string ReadInformationalVersion()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0-unknown";
        }

        // MSBuild appends "+<commit sha>" to the informational version.
        var revisionSeparator = informational.IndexOf('+', StringComparison.Ordinal);
        return revisionSeparator < 0 ? informational : informational[..revisionSeparator];
    }
}
