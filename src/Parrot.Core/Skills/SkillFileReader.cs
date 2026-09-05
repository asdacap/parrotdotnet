using System.Text;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Skills;

internal sealed class SkillFileReader
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Read(string lexicalPath, string physicalPath, SecurityProfile security)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lexicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentNullException.ThrowIfNull(security);

        var lexical = Path.GetFullPath(lexicalPath);
        var physical = Path.GetFullPath(physicalPath);
        if (!ToolWorkspace.AllowsRead((lexical, physical), security))
        {
            throw new UnauthorizedAccessException($"Read access denied for '{lexicalPath}'.");
        }

        if (FileMutation.Inspect(physical) != FileMutationEntryKind.Regular)
        {
            throw new InvalidOperationException($"Skill source '{lexicalPath}' is not a regular file.");
        }

        using var stream = Open(physical);
        ValidateOpenedTarget(stream, lexical, physical, security);
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException($"Skill source '{lexicalPath}' exceeds the 1 MiB limit.");
        }

        var byteLength = checked((int)stream.Length);
        var bytes = new byte[byteLength];
        stream.ReadExactly(bytes);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException failure)
        {
            throw new InvalidDataException($"Skill source '{lexicalPath}' is not valid UTF-8.", failure);
        }
    }

    private static FileStream Open(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
        }

        var workspace = new ToolWorkspace(Path.GetPathRoot(path)
            ?? throw new InvalidOperationException($"Skill source '{path}' has no filesystem root."));
        return workspace.OpenRegularReadWithoutLinks(path, SecurityProfile.Compose(readOnly: true, [], [], []));
    }

    private static void ValidateOpenedTarget(
        FileStream stream,
        string lexical,
        string physical,
        SecurityProfile security)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (FileMutation.Inspect(physical) != FileMutationEntryKind.Regular)
            {
                throw new InvalidOperationException($"Skill source '{lexical}' is not a regular file.");
            }

            return;
        }

        var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
        var target = new FileInfo($"/proc/self/fd/{descriptor}").ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"Cannot resolve opened skill source '{lexical}'.");
        var openedPhysical = Path.GetFullPath(target.FullName);
        if (!string.Equals(physical, openedPhysical, StringComparison.Ordinal)
            || !ToolWorkspace.AllowsRead((lexical, openedPhysical), security))
        {
            throw new UnauthorizedAccessException($"Read access denied for '{lexical}'.");
        }
    }
}
