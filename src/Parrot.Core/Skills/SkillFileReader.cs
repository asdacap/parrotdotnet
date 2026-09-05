using System.Text;
using Parrot.Tools;

namespace Parrot.Skills;

internal sealed class SkillFileReader
{
    private const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Read(string lexicalPath, string physicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lexicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        var lexical = Path.GetFullPath(lexicalPath);
        var physical = PlatformPath.Normalize(physicalPath);
        if (FileMutation.Inspect(physical) != FileMutationEntryKind.Regular)
        {
            throw new InvalidOperationException($"Skill source '{lexicalPath}' is not a regular file.");
        }

        using var stream = new FileStream(
            physical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        ValidateOpenedTarget(stream, lexical, physical);
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

    private static void ValidateOpenedTarget(FileStream stream, string lexical, string physical)
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
        if (!string.Equals(physical, openedPhysical, StringComparison.Ordinal))
        {
            throw new IOException($"Skill source '{lexical}' changed while it was being opened.");
        }
    }
}
