namespace Parrot.Files;

internal static class PrivateFile
{
    private const int MaximumNameAttempts = 16;

    public static FileStream CreateNew(string path, FileShare share, FileOptions options)
    {
        var streamOptions = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = share,
            Options = options,
        };

        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, streamOptions);
    }

    public static FileStream CreateUnique(string directory, Func<string> nextName, string kind, FileOptions options)
    {
        EnsureDirectory(directory);

        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            var name = nextName();

            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
                || !name.EndsWith("-arse.dat", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The {kind} name must be a safe -arse.dat basename.");
            }

            var path = Path.Combine(directory, name);

            try
            {
                return CreateNew(path, FileShare.Read, options);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        throw new IOException($"Could not create a unique {kind} in '{directory}'.");
    }

    public static void EnsureDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
