using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Parrot.Security;

namespace Parrot.Tools;

internal sealed partial class ToolWorkspace(string workingDirectory)
{
    private const int DarwinPathLength = 1024;
    private const int DarwinVnodePathOffset = 176;
    private const int DarwinVnodePathInfoLength = DarwinVnodePathOffset + DarwinPathLength;
    private const int DarwinVnodePathInfo = 2;

    public string Root { get; } = Canonicalize(workingDirectory);

    public static bool AllowsRead((string Lexical, string Physical) path, SecurityProfile security) =>
        security.AllowsRead(path.Lexical) && security.AllowsRead(path.Physical);

    public (string Lexical, string Physical) ResolveRead(string path)
    {
        var lexical = ResolveLexical(path);
        var physical = ResolveLinks(lexical);
        return (lexical, physical);
    }

    public FileStream OpenRegularReadWithoutLinks(string path, SecurityProfile security)
    {
        var lexical = ResolveLexical(path);
        ValidateRegularReadWithoutLinks(lexical, path);
        var stream = new FileStream(
            lexical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            var physical = ResolveDescriptorPath(stream, path);
            ValidateRegularReadWithoutLinks(lexical, path);
            if (!string.Equals(lexical, physical, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Path '{path}' does not identify the opened regular file without links.");
            }

            if (!AllowsRead((lexical, physical), security))
            {
                throw new UnauthorizedAccessException($"Read access denied for '{path}'.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public ToolMutationPath ResolveMutation(string path, bool create, SecurityProfile security)
    {
        var lexical = ResolveLexical(path);
        var physical = ResolveMutationPath(lexical, path, create);

        if (!security.AllowsWrite(lexical) || !security.AllowsWrite(physical))
        {
            throw new InvalidOperationException($"Write access denied for '{path}'.");
        }

        if (create)
        {
            RequireWritableMissingParents(physical, path, security);
        }

        return new ToolMutationPath(physical, DisplayPath(physical));
    }

    private static void ValidateRegularReadWithoutLinks(string lexical, string requested)
    {
        var root = Path.GetPathRoot(lexical) ?? throw new InvalidOperationException($"Invalid path '{requested}'.");
        var relative = Path.GetRelativePath(root, lexical);
        var current = Path.TrimEndingDirectorySeparator(root);

        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var kind = FileMutation.Inspect(current);
            if (kind == FileMutationEntryKind.SymbolicLink)
            {
                throw new InvalidOperationException($"Path '{requested}' traverses a symbolic link.");
            }

            if (kind == FileMutationEntryKind.Missing)
            {
                throw new FileNotFoundException($"Source '{requested}' is missing.");
            }
        }

        FileMutation.RequireRegularFile(lexical);
    }

    private static string ResolveMutationPath(string full, string requested, bool create)
    {
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException($"Invalid path '{requested}'.");
        var relative = Path.GetRelativePath(root, full);
        var current = Path.TrimEndingDirectorySeparator(root);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            var kind = FileMutation.Inspect(current);
            if (kind == FileMutationEntryKind.Missing)
            {
                if (create)
                {
                    break;
                }

                throw new FileNotFoundException($"Source '{requested}' is missing.");
            }

            if (kind == FileMutationEntryKind.SymbolicLink)
            {
                throw new InvalidOperationException($"Path '{requested}' traverses a symbolic link.");
            }

            if (index < parts.Length - 1 && kind != FileMutationEntryKind.Directory)
            {
                throw new InvalidOperationException($"Parent of '{requested}' is not a directory.");
            }
        }

        return full;
    }

    private static string ResolveDescriptorPath(FileStream stream, string requested)
    {
        if (OperatingSystem.IsMacOS())
        {
            return ResolveDarwinDescriptorPath(stream);
        }

        var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt64();
        var descriptorPath = $"/proc/self/fd/{descriptor}";
        var target = new FileInfo(descriptorPath).ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new IOException($"Cannot resolve opened source '{requested}'.");
        return Path.GetFullPath(target.FullName);
    }

    private static unsafe string ResolveDarwinDescriptorPath(FileStream stream)
    {
        var buffer = stackalloc byte[DarwinVnodePathInfoLength];
        if (ProcPidFdInfo(
            Environment.ProcessId,
            stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
            DarwinVnodePathInfo,
            buffer,
            DarwinVnodePathInfoLength) != DarwinVnodePathInfoLength)
        {
            throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        var bytes = new ReadOnlySpan<byte>(buffer + DarwinVnodePathOffset, DarwinPathLength);
        var length = bytes.IndexOf((byte)0);
        if (length == 0)
        {
            throw new IOException("Cannot resolve the opened source path on macOS.");
        }

        if (length < 0)
        {
            throw new IOException("The opened source path exceeds the macOS path limit.");
        }

        return PlatformPath.Normalize(Encoding.UTF8.GetString(bytes[..length]));
    }

    private static void RequireWritableMissingParents(string path, string requested, SecurityProfile security)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
        {
            if (!security.AllowsWrite(parent))
            {
                throw new InvalidOperationException($"Write access denied for '{requested}'.");
            }
        }
    }

    private static string Canonicalize(string path) =>
        Path.TrimEndingDirectorySeparator(ResolveLinks(PlatformPath.Normalize(path)));

    private static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full) ?? throw new InvalidOperationException($"Invalid path '{full}'.");
        var relative = Path.GetRelativePath(root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.TrimEndingDirectorySeparator(root);

        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if (!Path.Exists(current))
            {
                continue;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo link = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            var target = link.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new FileNotFoundException($"Symbolic link target for '{current}' is missing.");
            current = target.FullName;
        }

        return current;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libproc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    private static unsafe partial int ProcPidFdInfo(
        int processId,
        int descriptor,
        int flavor,
        byte* buffer,
        int bufferSize);

    private string ResolveLexical(string path) => PlatformPath.Normalize(
        Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path));

    private string DisplayPath(string physical) => Path.GetRelativePath(Root, physical);
}
