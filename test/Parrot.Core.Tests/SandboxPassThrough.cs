namespace Parrot.Core.Tests;

internal static class SandboxPassThrough
{
    public static string Write(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(directory, $"sandbox-{Guid.NewGuid():n}");
        var script = "#!/bin/sh\nhelper=\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; "
            + "elif [ \"$1\" = \"--ro-bind\" ] && [ \"$2\" = \"$3\" ] "
            + "&& [ \"$(basename \"$2\")\" = \"parrot-pty-attach\" ]; "
            + "then helper=$2; shift 2; fi\n  shift\ndone\nshift\n"
            + "if [ \"$1\" = \"$helper\" ] && [ -n \"$helper\" ]; then shift; exec \"$helper\" \"$@\"; fi\n"
            + "exec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var scriptFile = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            scriptFile.Flush(flushToDisk: true);
        }

        return path;
    }
}
