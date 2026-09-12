using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Parrot.Context;
using Parrot.Process;

namespace Parrot.Store;

internal sealed partial class CompactionGroupBlobStore
{
    private const int MaximumNameAttempts = 16;
    private const int CurrentDirectoryFileDescriptor = -100;
    private const int WriteOnly = 0x1;
    private const int LinuxCreate = 0x40;
    private const int LinuxExclusive = 0x80;
    private const int LinuxDirectory = 0x10000;
    private const int LinuxNoFollow = 0x20000;
    private const int LinuxCloseOnExec = 0x80000;
    private const int LinuxTemporaryFile = 0x410000;
    private const int DarwinCreate = 0x200;
    private const int DarwinExclusive = 0x800;
    private const int DarwinNoFollow = 0x100;
    private const int DarwinDirectory = 0x100000;
    private const int DarwinCloseOnExec = 0x1000000;
    private const int EmptyPath = 0x1000;
    private const int EmptyPathLink = 0x1000;
    private const int LinuxNoFollowStatus = 0x100;
    private const int DarwinNoFollowStatus = 0x20;
    private const uint StatusType = 0x1;
    private const uint StatusInode = 0x100;
    private const ushort FileTypeMask = 0xf000;
    private const ushort DirectoryFileType = 0x4000;
    private const uint PrivateFileMode = 0x180;
    private const int NoEntry = 2;
    private const int InvalidArgument = 22;
    private const int IsDirectory = 21;
    private const int NotImplemented = 38;
    private const int OperationNotSupported = 95;
    private const int AlreadyExists = 17;
    private const uint LinuxRenameExchange = 2;
    private const uint DarwinRenameSwap = 2;
    private const int LinuxRemoveDirectory = 0x200;
    private const int DarwinRemoveDirectory = 0x80;
    private const uint PrivateDirectoryMode = 0x1c0;
    private readonly AgentScratchDirectory _scratch;
    private readonly string _blobDirectory;
    private readonly Func<string> _nextName;
    private readonly Action _afterBlobDirectoryOpened;
    private readonly Action _afterFileCreated;
    private readonly Action _afterFinalLinkCreated;
    private readonly Action<string> _afterCleanupSelection;
    private readonly Func<uint, uint> _returnedStatusMask;
    private readonly CompactionGroupBlobStorePlatform _platform;
    private readonly bool _forceNamedFile;
    private DirectoryIdentity? _blobIdentity;

    public CompactionGroupBlobStore(AgentScratchDirectory scratch)
        : this(
            scratch,
            static () => new HaikunatorNameGenerator().Next().Replace("-arse.dat", ".json", StringComparison.Ordinal),
            static () => { },
            static () => { },
            static () => { },
            DetectPlatform(),
            false,
            static _ => { },
            static mask => mask)
    {
    }

    internal CompactionGroupBlobStore(AgentScratchDirectory scratch, Func<string> nextName)
        : this(
            scratch,
            nextName,
            static () => { },
            static () => { },
            static () => { },
            DetectPlatform(),
            false,
            static _ => { },
            static mask => mask)
    {
    }

    internal CompactionGroupBlobStore(
        AgentScratchDirectory scratch,
        Func<string> nextName,
        Action afterBlobDirectoryOpened,
        Action afterFileCreated)
        : this(
            scratch,
            nextName,
            afterBlobDirectoryOpened,
            afterFileCreated,
            static () => { },
            DetectPlatform(),
            false,
            static _ => { },
            static mask => mask)
    {
    }

    internal CompactionGroupBlobStore(
        AgentScratchDirectory scratch,
        Func<string> nextName,
        Action afterBlobDirectoryOpened,
        Action afterFileCreated,
        CompactionGroupBlobStorePlatform platform,
        bool forceNamedFile)
        : this(
            scratch,
            nextName,
            afterBlobDirectoryOpened,
            afterFileCreated,
            static () => { },
            platform,
            forceNamedFile,
            static _ => { },
            static mask => mask)
    {
    }

    internal CompactionGroupBlobStore(
        AgentScratchDirectory scratch,
        Func<string> nextName,
        Action afterBlobDirectoryOpened,
        Action afterFileCreated,
        Action afterFinalLinkCreated,
        CompactionGroupBlobStorePlatform platform,
        bool forceNamedFile)
        : this(
            scratch,
            nextName,
            afterBlobDirectoryOpened,
            afterFileCreated,
            afterFinalLinkCreated,
            platform,
            forceNamedFile,
            static _ => { },
            static mask => mask)
    {
    }

    internal CompactionGroupBlobStore(
        AgentScratchDirectory scratch,
        Func<string> nextName,
        Action afterBlobDirectoryOpened,
        Action afterFileCreated,
        Action afterFinalLinkCreated,
        CompactionGroupBlobStorePlatform platform,
        bool forceNamedFile,
        Action<string> afterCleanupSelection,
        Func<uint, uint> returnedStatusMask)
    {
        _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));
        _blobDirectory = PlatformPath.Normalize(scratch.BlobDirectory);
        _nextName = nextName ?? throw new ArgumentNullException(nameof(nextName));
        _afterBlobDirectoryOpened = afterBlobDirectoryOpened
            ?? throw new ArgumentNullException(nameof(afterBlobDirectoryOpened));
        _afterFileCreated = afterFileCreated ?? throw new ArgumentNullException(nameof(afterFileCreated));
        _afterFinalLinkCreated = afterFinalLinkCreated
            ?? throw new ArgumentNullException(nameof(afterFinalLinkCreated));
        _afterCleanupSelection = afterCleanupSelection
            ?? throw new ArgumentNullException(nameof(afterCleanupSelection));
        _returnedStatusMask = returnedStatusMask ?? throw new ArgumentNullException(nameof(returnedStatusMask));
        _platform = platform;
        _forceNamedFile = forceNamedFile;
        ValidateBlobDirectoryPath();
        _blobIdentity = null;
    }

    public async Task<string> Persist(CompactionGroup group, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(group);
        ValidateBlobDirectoryPath();
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform == CompactionGroupBlobStorePlatform.Unsupported)
        {
            throw new PlatformNotSupportedException(
                "Race-safe compaction group persistence is unavailable on this platform.");
        }

        var artifact = CompactionGroupArtifact.From(group);
        using var directory = OpenDirectory(_blobDirectory, _platform);
        _afterBlobDirectoryOpened();
        RequireCurrent(directory);

        if (_platform == CompactionGroupBlobStorePlatform.Linux && !_forceNamedFile)
        {
            var path = await TryPersistUnnamed(
                directory,
                artifact,
                cancellationToken).ConfigureAwait(false);
            if (path is not null)
            {
                return path;
            }
        }

        return await PersistNamed(directory, artifact, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateName(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
            || !name.EndsWith(".json", StringComparison.Ordinal)
            || name.Length == ".json".Length)
        {
            throw new InvalidOperationException("The compaction group blob name must be a safe .json basename.");
        }
    }

    private static SafeFileHandle OpenDirectory(
        string absolutePath,
        CompactionGroupBlobStorePlatform platform)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new InvalidOperationException("The compaction group blob directory must be absolute.");
        }

        var flags = GetDirectoryFlags(platform);
        var rootDescriptor = OpenAt(
            CurrentDirectoryFileDescriptor,
            Path.DirectorySeparatorChar.ToString(),
            flags,
            0);
        if (rootDescriptor < 0)
        {
            throw InvalidDirectory("open the filesystem root");
        }

        SafeFileHandle? directory = new((nint)rootDescriptor, ownsHandle: true);
        try
        {
            foreach (var component in absolutePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries))
            {
                var nextDescriptor = OpenAt(directory, component, flags, 0);
                if (nextDescriptor < 0)
                {
                    throw InvalidDirectory("open the compaction group blob directory");
                }

                directory.Dispose();
                directory = new SafeFileHandle((nint)nextDescriptor, ownsHandle: true);
            }

            var result = directory;
            directory = null;
            return result;
        }
        finally
        {
            directory?.Dispose();
        }
    }

    private static int GetDirectoryFlags(CompactionGroupBlobStorePlatform platform) =>
        platform switch
        {
            CompactionGroupBlobStorePlatform.Linux => LinuxDirectory | LinuxNoFollow | LinuxCloseOnExec,
            CompactionGroupBlobStorePlatform.Darwin => DarwinDirectory | DarwinNoFollow | DarwinCloseOnExec,
            _ => throw new PlatformNotSupportedException(
                "Race-safe compaction group persistence is unavailable on this platform."),
        };

    private static DirectoryIdentity Identify(
        SafeFileHandle directory,
        CompactionGroupBlobStorePlatform platform,
        Func<uint, uint> returnedStatusMask)
    {
        if (platform == CompactionGroupBlobStorePlatform.Linux)
        {
            const uint requiredMask = StatusType | StatusInode;
            if (Statx(directory, string.Empty, EmptyPath, requiredMask, out var status) != 0)
            {
                throw NativeFailure("inspect the compaction group blob directory");
            }

            RequireStatusMask(status, requiredMask, returnedStatusMask);
            if ((status.Mode & FileTypeMask) != DirectoryFileType)
            {
                throw new InvalidOperationException("The compaction group blob path is not a directory.");
            }

            return new DirectoryIdentity(
                ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
                status.Inode);
        }

        if (FStat(directory, out var darwinStatus) != 0)
        {
            throw NativeFailure("inspect the compaction group blob directory");
        }

        if ((darwinStatus.Mode & FileTypeMask) != DirectoryFileType)
        {
            throw new InvalidOperationException("The compaction group blob path is not a directory.");
        }

        return new DirectoryIdentity(unchecked((uint)darwinStatus.Device), darwinStatus.Inode);
    }

    private static FileStream? TryOpenTemporaryFile(SafeFileHandle directory)
    {
        var descriptor = OpenAt(
            directory,
            ".",
            WriteOnly | LinuxTemporaryFile | LinuxNoFollow | LinuxCloseOnExec,
            PrivateFileMode);
        if (descriptor >= 0)
        {
            return OpenStream(descriptor);
        }

        var error = Marshal.GetLastPInvokeError();
        return error is NoEntry or IsDirectory or InvalidArgument or NotImplemented or OperationNotSupported
            ? null
            : throw new IOException($"Failed to create an unnamed compaction group staging file: {new Win32Exception(error).Message}");
    }

    private static FileStream? TryOpenNamedFile(
        SafeFileHandle directory,
        string name,
        CompactionGroupBlobStorePlatform platform)
    {
        var flags = platform == CompactionGroupBlobStorePlatform.Linux
            ? WriteOnly | LinuxCreate | LinuxExclusive | LinuxNoFollow | LinuxCloseOnExec
            : WriteOnly | DarwinCreate | DarwinExclusive | DarwinNoFollow | DarwinCloseOnExec;
        var descriptor = OpenAt(directory, name, flags, PrivateFileMode);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == AlreadyExists)
            {
                return null;
            }

            throw new IOException($"Failed to create a compaction group artifact: {new Win32Exception(error).Message}");
        }

        return OpenStream(descriptor);
    }

    private static FileStream OpenStream(int descriptor)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            if (FChmod(handle, PrivateFileMode) != 0)
            {
                throw NativeFailure("secure a compaction group artifact");
            }

            var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
            handle = null;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static bool PublishUnnamed(SafeFileHandle temporaryFile, SafeFileHandle directory, string name) =>
        Publish(temporaryFile, string.Empty, directory, name, EmptyPathLink);

    private static bool PublishNamed(SafeFileHandle directory, string stagingName, string name) =>
        Publish(directory, stagingName, directory, name, 0);

    private static bool Publish(
        SafeFileHandle existingDirectory,
        string existingName,
        SafeFileHandle newDirectory,
        string newName,
        int flags)
    {
        if (LinkAt(existingDirectory, existingName, newDirectory, newName, flags) == 0)
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == AlreadyExists)
        {
            return false;
        }

        throw new IOException($"Failed to publish a compaction group artifact: {new Win32Exception(error).Message}");
    }

    private static void DeleteArtifactIfSame(
        SafeFileHandle directory,
        string name,
        FileIdentity expectedIdentity,
        CompactionGroupBlobStorePlatform platform,
        Action<string> afterCleanupSelection,
        Func<uint, uint> returnedStatusMask)
    {
        for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            var quarantineName = $".{Guid.NewGuid():n}.cleanup";
            var selection = SelectForCleanup(directory, name, quarantineName, platform);
            if (selection == CleanupSelection.Collision)
            {
                continue;
            }

            if (selection == CleanupSelection.Missing)
            {
                return;
            }

            try
            {
                afterCleanupSelection(name);
                var identity = TryIdentifyEntry(directory, quarantineName, platform, returnedStatusMask);
                if (identity == expectedIdentity)
                {
                    if (UnlinkAt(directory, quarantineName, 0) != 0)
                    {
                        throw NativeFailure("remove a quarantined compaction group artifact");
                    }

                    RemovePlaceholder(directory, name, platform);
                    return;
                }
            }
            catch
            {
                RestoreQuarantinedEntry(directory, quarantineName, name, platform);
                RemovePlaceholder(directory, quarantineName, platform);
                throw;
            }

            RestoreQuarantinedEntry(directory, quarantineName, name, platform);
            RemovePlaceholder(directory, quarantineName, platform);
            return;
        }

        throw new IOException("Could not reserve a unique quarantine name for compaction group cleanup.");
    }

    private static CleanupSelection SelectForCleanup(
        SafeFileHandle directory,
        string name,
        string quarantineName,
        CompactionGroupBlobStorePlatform platform)
    {
        if (MakeDirectoryAt(directory, quarantineName, PrivateDirectoryMode) != 0)
        {
            var createError = Marshal.GetLastPInvokeError();
            return createError == AlreadyExists
                ? CleanupSelection.Collision
                : throw new IOException($"Failed to reserve a compaction group cleanup placeholder: {new Win32Exception(createError).Message}");
        }

        var result = platform == CompactionGroupBlobStorePlatform.Linux
            ? RenameAt2(directory, name, directory, quarantineName, LinuxRenameExchange)
            : RenameAtX(directory, name, directory, quarantineName, DarwinRenameSwap);
        if (result == 0)
        {
            return CleanupSelection.Selected;
        }

        var error = Marshal.GetLastPInvokeError();
        RemovePlaceholder(directory, quarantineName, platform);
        return error == NoEntry
            ? CleanupSelection.Missing
            : throw new IOException($"Failed to quarantine a compaction group artifact: {new Win32Exception(error).Message}");
    }

    private static void RestoreQuarantinedEntry(
        SafeFileHandle directory,
        string quarantineName,
        string name,
        CompactionGroupBlobStorePlatform platform)
    {
        var result = platform == CompactionGroupBlobStorePlatform.Linux
            ? RenameAt2(directory, quarantineName, directory, name, LinuxRenameExchange)
            : RenameAtX(directory, quarantineName, directory, name, DarwinRenameSwap);
        if (result != 0)
        {
            throw NativeFailure("restore an unrelated quarantined entry");
        }
    }

    private static void RemovePlaceholder(
        SafeFileHandle directory,
        string name,
        CompactionGroupBlobStorePlatform platform)
    {
        var flags = platform == CompactionGroupBlobStorePlatform.Linux
            ? LinuxRemoveDirectory
            : DarwinRemoveDirectory;
        if (UnlinkAt(directory, name, flags) != 0)
        {
            throw NativeFailure("remove a compaction group cleanup placeholder");
        }
    }

    private static FileIdentity IdentifyFile(
        SafeFileHandle file,
        CompactionGroupBlobStorePlatform platform,
        Func<uint, uint> returnedStatusMask)
    {
        if (platform == CompactionGroupBlobStorePlatform.Linux)
        {
            const uint requiredMask = StatusType | StatusInode;
            if (Statx(file, string.Empty, EmptyPath, requiredMask, out var status) != 0)
            {
                throw NativeFailure("inspect a compaction group artifact");
            }

            RequireStatusMask(status, requiredMask, returnedStatusMask);
            return new FileIdentity(
                ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
                status.Inode);
        }

        if (FStat(file, out var darwinStatus) != 0)
        {
            throw NativeFailure("inspect a compaction group artifact");
        }

        return new FileIdentity(unchecked((uint)darwinStatus.Device), darwinStatus.Inode);
    }

    private static FileIdentity? TryIdentifyEntry(
        SafeFileHandle directory,
        string name,
        CompactionGroupBlobStorePlatform platform,
        Func<uint, uint> returnedStatusMask)
    {
        if (platform == CompactionGroupBlobStorePlatform.Linux)
        {
            const uint requiredMask = StatusType | StatusInode;
            if (Statx(directory, name, LinuxNoFollowStatus, requiredMask, out var status) == 0)
            {
                RequireStatusMask(status, requiredMask, returnedStatusMask);
                return new FileIdentity(
                    ((ulong)status.DeviceMajor << 32) | status.DeviceMinor,
                    status.Inode);
            }
        }
        else if (FStatAt(directory, name, out var darwinStatus, DarwinNoFollowStatus) == 0)
        {
            return new FileIdentity(unchecked((uint)darwinStatus.Device), darwinStatus.Inode);
        }

        var error = Marshal.GetLastPInvokeError();
        return error == NoEntry
            ? null
            : throw new IOException($"Failed to inspect a compaction group artifact: {new Win32Exception(error).Message}");
    }

    private static void RequireStatusMask(
        LinuxStatx status,
        uint requiredMask,
        Func<uint, uint> returnedStatusMask)
    {
        var returnedMask = returnedStatusMask(status.Mask);
        if ((returnedMask & requiredMask) != requiredMask)
        {
            throw new IOException("The operating system did not return all required compaction group identity fields.");
        }
    }

    private static void TryDeleteArtifact(
        SafeFileHandle directory,
        string name,
        FileIdentity expectedIdentity,
        CompactionGroupBlobStorePlatform platform,
        Action<string> afterCleanupSelection,
        Func<uint, uint> returnedStatusMask,
        Exception failure)
    {
        try
        {
            DeleteArtifactIfSame(
                directory,
                name,
                expectedIdentity,
                platform,
                afterCleanupSelection,
                returnedStatusMask);
        }
        catch (Exception cleanupFailure)
        {
            ObserveCleanupFailure(failure, cleanupFailure);
        }
    }

    private static void ObserveCleanupFailure(Exception failure, Exception cleanupFailure) =>
        failure.Data["CompactionGroupArtifactCleanupFailure"] = cleanupFailure;

    private static InvalidOperationException InvalidDirectory(string operation) =>
        new($"Failed to {operation}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    private static IOException NativeFailure(string operation) =>
        new($"Failed to {operation}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(
        SafeFileHandle directory,
        string path,
        int flags,
        uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "linkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinkAt(
        SafeFileHandle existingDirectory,
        string existingName,
        SafeFileHandle newDirectory,
        string newName,
        int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int MakeDirectoryAt(SafeFileHandle directory, string path, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(SafeFileHandle directory, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAt2(
        SafeFileHandle existingDirectory,
        string existingName,
        SafeFileHandle newDirectory,
        string newName,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "renameatx_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameAtX(
        SafeFileHandle existingDirectory,
        string existingName,
        SafeFileHandle newDirectory,
        string newName,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(
        SafeFileHandle directory,
        string path,
        int flags,
        uint mask,
        out LinuxStatx status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int FChmod(SafeFileHandle descriptor, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStat(SafeFileHandle descriptor, out DarwinStat status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fstatat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int FStatAt(
        SafeFileHandle directory,
        string path,
        out DarwinStat status,
        int flags);

    private static async Task Serialize(
        Stream stream,
        CompactionGroupArtifact artifact,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(
            stream,
            artifact,
            CompactionGroupJsonContext.Default.CompactionGroupArtifact,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CompactionGroupBlobStorePlatform DetectPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return CompactionGroupBlobStorePlatform.Linux;
        }

        return OperatingSystem.IsMacOS()
            ? CompactionGroupBlobStorePlatform.Darwin
            : CompactionGroupBlobStorePlatform.Unsupported;
    }

    private async Task<string?> TryPersistUnnamed(
        SafeFileHandle directory,
        CompactionGroupArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var temporaryFile = TryOpenTemporaryFile(directory);
        if (temporaryFile is null)
        {
            return null;
        }

        FileIdentity? temporaryIdentity = null;
        string? publishedName = null;
        try
        {
            await using (temporaryFile.ConfigureAwait(false))
            {
                _afterFileCreated();
                await Serialize(temporaryFile, artifact, cancellationToken).ConfigureAwait(false);
                RequireCurrent(directory);
                temporaryIdentity = IdentifyFile(temporaryFile.SafeFileHandle, _platform, _returnedStatusMask);

                for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = _nextName();
                    ValidateName(name);
                    var path = Confine(name);
                    if (!PublishUnnamed(temporaryFile.SafeFileHandle, directory, name))
                    {
                        continue;
                    }

                    publishedName = name;
                    _afterFinalLinkCreated();
                    RequireCurrent(directory);
                    return path;
                }
            }

            throw new IOException($"Could not create a unique compaction group blob in '{_blobDirectory}'.");
        }
        catch (Exception failure)
        {
            if (publishedName is not null && temporaryIdentity is not null)
            {
                TryDeleteArtifact(
                    directory,
                    publishedName,
                    temporaryIdentity.Value,
                    _platform,
                    _afterCleanupSelection,
                    _returnedStatusMask,
                    failure);
            }

            throw;
        }
    }

    private async Task<string> PersistNamed(
        SafeFileHandle directory,
        CompactionGroupArtifact artifact,
        CancellationToken cancellationToken)
    {
        string stagingName;
        FileStream? stagingCandidate;
        do
        {
            stagingName = $".{Guid.NewGuid():n}.tmp";
            stagingCandidate = TryOpenNamedFile(directory, stagingName, _platform);
        }
        while (stagingCandidate is null);
        var stagingFile = stagingCandidate;
        FileIdentity? stagingIdentity = null;
        string? publishedName = null;

        try
        {
            await using (stagingFile.ConfigureAwait(false))
            {
                stagingIdentity = IdentifyFile(stagingFile.SafeFileHandle, _platform, _returnedStatusMask);
                _afterFileCreated();
                await Serialize(stagingFile, artifact, cancellationToken).ConfigureAwait(false);
                RequireCurrent(directory);

                for (var attempt = 0; attempt < MaximumNameAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = _nextName();
                    ValidateName(name);
                    var path = Confine(name);
                    if (!PublishNamed(directory, stagingName, name))
                    {
                        continue;
                    }

                    publishedName = name;
                    _afterFinalLinkCreated();
                    RequireCurrent(directory);
                    DeleteArtifactIfSame(
                        directory,
                        stagingName,
                        stagingIdentity.Value,
                        _platform,
                        _afterCleanupSelection,
                        _returnedStatusMask);
                    return path;
                }
            }

            throw new IOException($"Could not create a unique compaction group blob in '{_blobDirectory}'.");
        }
        catch (Exception failure)
        {
            if (stagingIdentity is not null)
            {
                if (publishedName is not null)
                {
                    TryDeleteArtifact(
                        directory,
                        publishedName,
                        stagingIdentity.Value,
                        _platform,
                        _afterCleanupSelection,
                        _returnedStatusMask,
                        failure);
                }

                TryDeleteArtifact(
                    directory,
                    stagingName,
                    stagingIdentity.Value,
                    _platform,
                    _afterCleanupSelection,
                    _returnedStatusMask,
                    failure);
            }

            throw;
        }
    }

    private string Confine(string name)
    {
        var path = PlatformPath.Normalize(Path.Combine(_blobDirectory, name));
        if (!_scratch.Contains(path)
            || !string.Equals(Path.GetDirectoryName(path), _blobDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A compaction group blob escaped the agent scratch blob directory.");
        }

        return path;
    }

    private void ValidateBlobDirectoryPath()
    {
        if (!_scratch.Contains(_blobDirectory)
            || !string.Equals(PlatformPath.Normalize(_scratch.BlobDirectory), _blobDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The compaction group blob directory escaped the agent scratch directory.");
        }
    }

    private void RequireCurrent(SafeFileHandle directory)
    {
        var openedIdentity = Identify(directory, _platform, _returnedStatusMask);
        if (_blobIdentity is null)
        {
            _blobIdentity = openedIdentity;
        }
        else if (openedIdentity != _blobIdentity)
        {
            throw new InvalidOperationException(
                "The compaction group blob directory is not the invoking agent's current blob directory.");
        }

        using var current = OpenDirectory(_blobDirectory, _platform);
        if (Identify(current, _platform, _returnedStatusMask) != _blobIdentity)
        {
            throw new InvalidOperationException(
                "The compaction group blob directory changed while publishing an artifact.");
        }
    }

    private readonly record struct DirectoryIdentity(ulong Device, ulong Inode);

    private readonly record struct FileIdentity(ulong Device, ulong Inode);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)]
        public uint Mask;

        [FieldOffset(28)]
        public ushort Mode;

        [FieldOffset(32)]
        public ulong Inode;

        [FieldOffset(136)]
        public uint DeviceMajor;

        [FieldOffset(140)]
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)]
        public int Device;

        [FieldOffset(4)]
        public ushort Mode;

        [FieldOffset(8)]
        public ulong Inode;
    }
}
