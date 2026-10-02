namespace Parrot.Skills;

// The packaged skills are embedded under the "skills/" logical-name prefix and
// extracted to a known directory. A file that is already there is left alone,
// so local edits survive and a newer binary only adds what is missing.
internal static class PackagedSkills
{
    private const string Prefix = "skills/";
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    public static void Extract(string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(destination);

        var assembly = typeof(PackagedSkills).Assembly;
        try
        {
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)))
            {
                var relativePath = name[Prefix.Length..];
                var target = Path.Combine(destination, relativePath);
                if (File.Exists(target))
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(target) ?? destination;
                _ = Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, Path.GetRandomFileName());
                using (var source = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"missing packaged skill resource {name}"))
                using (var file = File.Create(temporary))
                {
                    source.CopyTo(file);
                }

                if (!OperatingSystem.IsWindows() && relativePath.Split('/').SkipLast(1).Contains("scripts"))
                {
                    File.SetUnixFileMode(temporary, Executable);
                }

                try
                {
                    File.Move(temporary, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another process extracted the same file first.
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("unable to write packaged skills", failure);
        }
    }
}
