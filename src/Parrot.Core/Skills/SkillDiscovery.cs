using Parrot.Config;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Skills;

internal sealed class SkillDiscovery
{
    private const int MaximumDepth = 6;
    private const int MaximumDirectories = 2000;
    private const int MaximumEntries = 20_000;
    private const int MaximumErrorLength = 1024;

    public static SkillSnapshot Discover(
        IReadOnlyList<SkillRoot> roots,
        SkillConfiguration configuration,
        SecurityProfile security)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(security);

        var discovered = new List<SkillMetadata>();
        var errors = new List<SkillLoadError>();
        var canonicalPaths = new HashSet<string>(PathComparer());
        foreach (var root in roots)
        {
            DiscoverRoot(root, configuration, security, discovered, errors, canonicalPaths);
        }

        return new SkillSnapshot([.. discovered], [.. errors]);
    }

    private static bool IsHiddenDirectory(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.StartsWith('.'))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsLink(string path) =>
        FileMutation.Inspect(path) == FileMutationEntryKind.SymbolicLink;

    private static string ResolvePhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new IOException("The skill path has no filesystem root.");
        var current = Path.TrimEndingDirectorySeparator(root);
        foreach (var part in Path.GetRelativePath(root, full)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (FileMutation.Inspect(current) != FileMutationEntryKind.SymbolicLink)
            {
                continue;
            }

            FileSystemInfo information = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            current = (information.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new FileNotFoundException($"Symbolic link target for '{current}' is missing."))
                .FullName;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void AddError(List<SkillLoadError> errors, string path, string message)
    {
        var normalized = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > MaximumErrorLength)
        {
            normalized = normalized[..MaximumErrorLength];
        }

        errors.Add(new(Path.GetFullPath(path), normalized.Length == 0 ? "Skill discovery failed." : normalized));
    }

    private static SkillDisplayMetadata ReadDisplayMetadata(
        DirectoryVisit skillDirectory,
        SecurityProfile security,
        List<SkillLoadError> errors)
    {
        var logicalPath = Path.Combine(skillDirectory.LogicalPath, "agents", "openai.yaml");
        if (!File.Exists(logicalPath))
        {
            return new(null, null);
        }

        try
        {
            var physicalPath = ResolvePhysicalPath(logicalPath);
            return SkillDisplayMetadataParser.Parse(SkillFileReader.Read(logicalPath, physicalPath, security));
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            AddError(errors, logicalPath, failure.Message);
            return new(null, null);
        }
    }

    private static void InspectDirectoryLink(
        SkillRoot root,
        SecurityProfile security,
        DirectoryVisit parent,
        string entry,
        List<SkillLoadError> errors,
        Queue<DirectoryVisit> pending)
    {
        if (!root.FollowDirectoryLinks || parent.Depth >= MaximumDepth)
        {
            return;
        }

        try
        {
            var information = new DirectoryInfo(entry);
            var target = information.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not DirectoryInfo targetDirectory || !targetDirectory.Exists || IsHiddenDirectory(entry))
            {
                return;
            }

            var physical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory.FullName));
            if (ToolWorkspace.AllowsRead((entry, physical), security))
            {
                pending.Enqueue(new(entry, physical, parent.Depth + 1));
            }
            else
            {
                AddError(errors, entry, "Read access denied.");
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            AddError(errors, entry, failure.Message);
        }
    }

    private static void LoadSkill(
        SkillRoot root,
        SkillConfiguration configuration,
        SecurityProfile security,
        DirectoryVisit parent,
        string discoveryPath,
        List<SkillMetadata> discovered,
        List<SkillLoadError> errors,
        HashSet<string> canonicalPaths)
    {
        var canonicalPath = Path.GetFullPath(Path.Combine(parent.PhysicalPath, "SKILL.md"));
        if (canonicalPaths.Contains(canonicalPath))
        {
            return;
        }

        try
        {
            var content = SkillFileReader.Read(discoveryPath, canonicalPath, security);
            var parsed = SkillFrontmatterParser.Parse(canonicalPath, content, root.Scope);
            var display = ReadDisplayMetadata(parent, security, errors);
            _ = canonicalPaths.Add(canonicalPath);
            discovered.Add(parsed with
            {
                DisplayName = display.DisplayName,
                ShortDescription = display.ShortDescription ?? parsed.ShortDescription,
                Enabled = configuration.Enabled && configuration.IsEnabled(canonicalPath),
                DiscoveryPath = Path.GetFullPath(discoveryPath),
            });
        }
        catch (Exception failure) when (failure is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or SkillParseException)
        {
            AddError(errors, discoveryPath, failure.Message);
        }
    }

    private static void InspectEntry(
        SkillRoot root,
        SkillConfiguration configuration,
        SecurityProfile security,
        DirectoryVisit parent,
        string entry,
        List<SkillMetadata> discovered,
        List<SkillLoadError> errors,
        HashSet<string> canonicalPaths,
        Queue<DirectoryVisit> pending)
    {
        FileMutationEntryKind kind;
        try
        {
            kind = FileMutation.Inspect(entry);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            AddError(errors, entry, failure.Message);
            return;
        }

        if (kind == FileMutationEntryKind.SymbolicLink)
        {
            InspectDirectoryLink(root, security, parent, entry, errors, pending);
            return;
        }

        if (kind == FileMutationEntryKind.Directory)
        {
            if (parent.Depth < MaximumDepth && !IsHiddenDirectory(entry))
            {
                var physical = Path.Combine(parent.PhysicalPath, Path.GetFileName(entry));
                if (ToolWorkspace.AllowsRead((entry, physical), security))
                {
                    pending.Enqueue(new(entry, physical, parent.Depth + 1));
                }
                else
                {
                    AddError(errors, entry, "Read access denied.");
                }
            }

            return;
        }

        if (kind == FileMutationEntryKind.Regular
            && string.Equals(Path.GetFileName(entry), "SKILL.md", StringComparison.Ordinal))
        {
            LoadSkill(root, configuration, security, parent, entry, discovered, errors, canonicalPaths);
        }
    }

    private static void DiscoverRoot(
        SkillRoot root,
        SkillConfiguration configuration,
        SecurityProfile security,
        List<SkillMetadata> discovered,
        List<SkillLoadError> errors,
        HashSet<string> canonicalPaths)
    {
        var logicalRoot = Path.GetFullPath(root.Path);
        if (!Path.Exists(logicalRoot))
        {
            return;
        }

        string physicalRoot;
        try
        {
            if (IsLink(logicalRoot) && !root.FollowDirectoryLinks)
            {
                AddError(errors, logicalRoot, "System skill roots cannot be symbolic links.");
                return;
            }

            physicalRoot = ResolvePhysicalPath(logicalRoot);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            AddError(errors, logicalRoot, failure.Message);
            return;
        }

        if (!ToolWorkspace.AllowsRead((logicalRoot, physicalRoot), security))
        {
            AddError(errors, logicalRoot, "Read access denied.");
            return;
        }

        var visited = new HashSet<string>(PathComparer());
        var pending = new Queue<DirectoryVisit>();
        pending.Enqueue(new(logicalRoot, physicalRoot, 0));
        var directoryCount = 0;
        var entryCount = 0;
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current.PhysicalPath))
            {
                continue;
            }

            directoryCount++;
            if (directoryCount > MaximumDirectories)
            {
                AddError(errors, logicalRoot, $"Skill discovery exceeded {MaximumDirectories} directories.");
                return;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current.LogicalPath);
                Array.Sort(entries, StringComparer.Ordinal);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                AddError(errors, current.LogicalPath, failure.Message);
                continue;
            }

            foreach (var entry in entries)
            {
                entryCount++;
                if (entryCount > MaximumEntries)
                {
                    AddError(errors, logicalRoot, $"Skill discovery exceeded {MaximumEntries} entries.");
                    return;
                }

                InspectEntry(root, configuration, security, current, entry, discovered, errors, canonicalPaths, pending);
            }
        }
    }

    private sealed record DirectoryVisit(string LogicalPath, string PhysicalPath, int Depth);
}
