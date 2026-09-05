using Parrot.Config;
using Parrot.Store;

namespace Parrot.Skills;

internal sealed class SkillCatalogFactory(Configuration configuration, string userHome, string packagedRoot)
{
    public SkillCatalog Create(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new SkillCatalog(
            ResolveRoots(workspace),
            () => (configuration.Skills, configuration.SkillsGeneration))
            .WithConfiguration(configuration.SetSkillEnabled);
    }

    internal IReadOnlyList<SkillRoot> ResolveRoots(ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var roots = new List<SkillRoot>();
        var paths = new HashSet<string>(PathComparer());
        if (workspace.RepositoryRoot is not null)
        {
            var launchDirectory = Contains(workspace.RepositoryRoot, workspace.LaunchDirectory)
                ? workspace.LaunchDirectory
                : workspace.PhysicalIdentity;
            for (var directory = new DirectoryInfo(launchDirectory);
                 directory is not null && Contains(workspace.RepositoryRoot, directory.FullName);
                 directory = directory.Parent)
            {
                AddRoot(roots, paths, Path.Combine(directory.FullName, ".agents", "skills"), SkillScope.Repo, true);
                if (PathsEqual(directory.FullName, workspace.RepositoryRoot))
                {
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(userHome))
        {
            AddRoot(roots, paths, Path.Combine(userHome, ".agents", "skills"), SkillScope.User, true);
        }

        AddRoot(roots, paths, packagedRoot, SkillScope.System, false);
        return roots;
    }

    private static void AddRoot(
        List<SkillRoot> roots,
        HashSet<string> paths,
        string path,
        SkillScope scope,
        bool followDirectoryLinks)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (paths.Add(full))
        {
            roots.Add(new(full, scope, followDirectoryLinks));
        }
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static bool PathsEqual(string left, string right) =>
        PathComparer().Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static StringComparer PathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
