using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Skills;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "parrot-skill-catalog-tests",
        Guid.NewGuid().ToString("n"))).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Roots_are_nearest_repository_then_user_then_packaged()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repository"));
        _ = Directory.CreateDirectory(Path.Combine(repository.FullName, ".git"));
        var launch = Directory.CreateDirectory(Path.Combine(repository.FullName, "src", "nested"));
        var user = Directory.CreateDirectory(Path.Combine(_root, "home"));
        var packaged = Directory.CreateDirectory(Path.Combine(_root, "packaged"));
        var factory = Factory(user.FullName, packaged.FullName);

        var roots = factory.ResolveRoots(ProjectWorkspace.FromLaunchDirectory(launch.FullName));

        var expected = new[]
        {
            Path.Combine(launch.FullName, ".agents", "skills"),
            Path.Combine(repository.FullName, "src", ".agents", "skills"),
            Path.Combine(repository.FullName, ".agents", "skills"),
            Path.Combine(user.FullName, ".agents", "skills"),
            packaged.FullName,
        };
        _ = await Assert.That(string.Join("|", roots.Select(root => root.Path))).IsEqualTo(string.Join("|", expected));
        _ = await Assert.That(string.Join(',', roots.Select(root => root.Scope))).IsEqualTo(
            "Repo,Repo,Repo,User,System");
    }

    [Test]
    public async Task Symlinked_launch_discovers_repository_roots_from_physical_workspace()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var repository = Directory.CreateDirectory(Path.Combine(_root, "physical-repository"));
        _ = Directory.CreateDirectory(Path.Combine(repository.FullName, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(repository.FullName, "nested"));
        var alias = Path.Combine(_root, "launch-alias");
        _ = Directory.CreateSymbolicLink(alias, nested.FullName);
        var user = Directory.CreateDirectory(Path.Combine(_root, "home"));
        var packaged = Directory.CreateDirectory(Path.Combine(_root, "packaged"));
        var workspace = ProjectWorkspace.FromLaunchDirectory(alias);

        var roots = Factory(user.FullName, packaged.FullName).ResolveRoots(workspace);

        _ = await Assert.That(workspace.RepositoryRoot).IsEqualTo(SecurityWriteTarget.Resolve(repository.FullName).Path);
        _ = await Assert.That(roots[0].Path).IsEqualTo(
            Path.Combine(SecurityWriteTarget.Resolve(nested.FullName).Path, ".agents", "skills"));
        _ = await Assert.That(roots[1].Path).IsEqualTo(
            Path.Combine(SecurityWriteTarget.Resolve(repository.FullName).Path, ".agents", "skills"));
    }

    [Test]
    public async Task Linked_worktree_discovers_from_worktree_but_keeps_common_repository_writable(
        CancellationToken cancellationToken)
    {
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repository"));
        var worktree = Directory.CreateDirectory(Path.Combine(_root, "worktree"));
        var gitDirectory = Directory.CreateDirectory(Path.Combine(repository.FullName, ".git", "worktrees", "linked"));
        await File.WriteAllTextAsync(
            Path.Combine(worktree.FullName, ".git"),
            $"gitdir: {gitDirectory.FullName}\n",
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(gitDirectory.FullName, "commondir"), "../..\n", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(gitDirectory.FullName, "gitdir"),
            Path.Combine(worktree.FullName, ".git") + "\n",
            cancellationToken);
        var workspace = ProjectWorkspace.FromLaunchDirectory(worktree.FullName);
        var user = Directory.CreateDirectory(Path.Combine(_root, "home"));
        var packaged = Directory.CreateDirectory(Path.Combine(_root, "packaged"));

        var roots = Factory(user.FullName, packaged.FullName).ResolveRoots(workspace);

        _ = await Assert.That(workspace.RepositoryRoot).IsEqualTo(worktree.FullName);
        _ = await Assert.That(workspace.WritableRoots.Contains(repository.FullName, StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(roots[0].Path).IsEqualTo(Path.Combine(worktree.FullName, ".agents", "skills"));
        _ = await Assert.That(roots.Any(root => root.Path.StartsWith(repository.FullName, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task No_repository_does_not_walk_launch_ancestors()
    {
        var launch = Directory.CreateDirectory(Path.Combine(_root, "plain", "nested"));
        var user = Directory.CreateDirectory(Path.Combine(_root, "home"));
        var packaged = Directory.CreateDirectory(Path.Combine(_root, "packaged"));

        var roots = Factory(user.FullName, packaged.FullName)
            .ResolveRoots(ProjectWorkspace.FromLaunchDirectory(launch.FullName));

        _ = await Assert.That(string.Join(',', roots.Select(root => root.Scope))).IsEqualTo("User,System");
    }

    [Test]
    public async Task Discovery_preserves_precedence_duplicates_and_disabled_listing(CancellationToken cancellationToken)
    {
        var firstRoot = Directory.CreateDirectory(Path.Combine(_root, "first"));
        var secondRoot = Directory.CreateDirectory(Path.Combine(_root, "second"));
        var firstPath = await WriteSkill(firstRoot.FullName, "first", "same", cancellationToken);
        var disabledPath = await WriteSkill(firstRoot.FullName, "disabled", "disabled", cancellationToken);
        _ = await WriteSkill(secondRoot.FullName, "second", "same", cancellationToken);
        var configuration = new SkillConfiguration(true, [new(disabledPath, false)]);
        var roots = new[]
        {
            new SkillRoot(firstRoot.FullName, SkillScope.Repo, true),
            new SkillRoot(secondRoot.FullName, SkillScope.User, true),
        };

        var snapshot = SkillDiscovery.Discover(roots, configuration);
        var selected = SkillSelection.Select(snapshot.Skills, SkillMentionParser.Parse("$same $disabled"));

        _ = await Assert.That(snapshot.Skills.Count).IsEqualTo(3);
        _ = await Assert.That(snapshot.Skills.Count(skill => skill.Name == "same")).IsEqualTo(2);
        _ = await Assert.That(snapshot.Skills.Single(
            skill => skill.Path == SecurityWriteTarget.Resolve(disabledPath).Path).Enabled).IsFalse();
        _ = await Assert.That(string.Join(',', selected.Select(skill => skill.Path)))
            .IsEqualTo(SecurityWriteTarget.Resolve(firstPath).Path);
    }

    [Test]
    public async Task Discovery_enforces_exact_name_depth_hidden_and_malformed_isolation(CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "skills"));
        _ = await WriteSkill(root.FullName, "valid", "valid", cancellationToken);
        _ = await WriteSkill(root.FullName, ".hidden", "hidden", cancellationToken);
        var malformedDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "malformed"));
        await File.WriteAllTextAsync(Path.Combine(malformedDirectory.FullName, "SKILL.md"), "not frontmatter", cancellationToken);
        var lowercaseDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "lowercase"));
        await File.WriteAllTextAsync(Path.Combine(lowercaseDirectory.FullName, "skill.md"), SkillContent("lowercase"), cancellationToken);
        var current = root.FullName;
        for (var depth = 1; depth <= 7; depth++)
        {
            current = Directory.CreateDirectory(Path.Combine(current, $"d{depth}")).FullName;
            await File.WriteAllTextAsync(Path.Combine(current, "SKILL.md"), SkillContent($"depth-{depth}"), cancellationToken);
        }

        var snapshot = SkillDiscovery.Discover(
            [new(root.FullName, SkillScope.User, true)],
            SkillConfiguration.Default);

        _ = await Assert.That(snapshot.Skills.Any(skill => skill.Name == "valid")).IsTrue();
        _ = await Assert.That(snapshot.Skills.Any(skill => skill.Name == "hidden")).IsFalse();
        _ = await Assert.That(snapshot.Skills.Any(skill => skill.Name == "lowercase")).IsFalse();
        _ = await Assert.That(snapshot.Skills.Any(skill => skill.Name == "depth-6")).IsTrue();
        _ = await Assert.That(snapshot.Skills.Any(skill => skill.Name == "depth-7")).IsFalse();
        _ = await Assert.That(snapshot.Errors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Discovery_follows_user_directory_links_deduplicates_cycles_and_ignores_file_links(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Directory.CreateDirectory(Path.Combine(_root, "skills"));
        var target = Directory.CreateDirectory(Path.Combine(_root, "target"));
        var canonical = await WriteSkill(target.FullName, "linked", "linked", cancellationToken);
        var alias = Path.Combine(root.FullName, "alias");
        _ = Directory.CreateSymbolicLink(alias, target.FullName);
        _ = Directory.CreateSymbolicLink(Path.Combine(target.FullName, "cycle"), root.FullName);
        _ = File.CreateSymbolicLink(Path.Combine(root.FullName, "SKILL.md"), canonical);

        var snapshot = SkillDiscovery.Discover(
            [new(root.FullName, SkillScope.User, true)],
            SkillConfiguration.Default);

        _ = await Assert.That(snapshot.Skills.Count).IsEqualTo(1);
        _ = await Assert.That(snapshot.Skills[0].Path).IsEqualTo(SecurityWriteTarget.Resolve(canonical).Path);
        _ = await Assert.That(snapshot.Skills[0].DiscoveryPath).IsEqualTo(Path.Combine(alias, "linked", "SKILL.md"));
    }

    [Test]
    public async Task System_directory_links_are_not_followed_and_linked_root_is_rejected(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var target = Directory.CreateDirectory(Path.Combine(_root, "target"));
        _ = await WriteSkill(target.FullName, "linked", "linked", cancellationToken);
        var root = Directory.CreateDirectory(Path.Combine(_root, "system"));
        _ = Directory.CreateSymbolicLink(Path.Combine(root.FullName, "alias"), target.FullName);
        var linkedRoot = Path.Combine(_root, "linked-system");
        _ = Directory.CreateSymbolicLink(linkedRoot, root.FullName);

        var childSnapshot = SkillDiscovery.Discover(
            [new(root.FullName, SkillScope.System, false)],
            SkillConfiguration.Default);
        var rootSnapshot = SkillDiscovery.Discover(
            [new(linkedRoot, SkillScope.System, false)],
            SkillConfiguration.Default);

        _ = await Assert.That(childSnapshot.Skills).IsEmpty();
        _ = await Assert.That(rootSnapshot.Skills).IsEmpty();
        _ = await Assert.That(rootSnapshot.Errors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Discovery_stops_at_directory_and_entry_limits()
    {
        var directoryRoot = Directory.CreateDirectory(Path.Combine(_root, "directory-limit"));
        for (var index = 0; index < 2_001; index++)
        {
            _ = Directory.CreateDirectory(Path.Combine(directoryRoot.FullName, $"directory-{index:D4}"));
        }

        var entryRoot = Directory.CreateDirectory(Path.Combine(_root, "entry-limit"));
        for (var index = 0; index < 20_001; index++)
        {
            using var entry = File.Create(Path.Combine(entryRoot.FullName, $"entry-{index:D5}"));
        }

        var directorySnapshot = SkillDiscovery.Discover(
            [new(directoryRoot.FullName, SkillScope.User, true)],
            SkillConfiguration.Default);
        var entrySnapshot = SkillDiscovery.Discover(
            [new(entryRoot.FullName, SkillScope.User, true)],
            SkillConfiguration.Default);

        _ = await Assert.That(directorySnapshot.Errors.Count).IsEqualTo(1);
        _ = await Assert.That(directorySnapshot.Errors[0].Message).Contains("2000 directories");
        _ = await Assert.That(entrySnapshot.Errors.Count).IsEqualTo(1);
        _ = await Assert.That(entrySnapshot.Errors[0].Message).Contains("20000 entries");
    }

    [Test]
    public async Task Discovery_isolates_oversized_and_invalid_utf8_files(CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "invalid-files"));
        var oversized = Directory.CreateDirectory(Path.Combine(root.FullName, "oversized"));
        var invalid = Directory.CreateDirectory(Path.Combine(root.FullName, "invalid"));
        await File.WriteAllBytesAsync(
            Path.Combine(oversized.FullName, "SKILL.md"),
            new byte[(1024 * 1024) + 1],
            cancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(invalid.FullName, "SKILL.md"),
            [0xFF],
            cancellationToken);

        var snapshot = SkillDiscovery.Discover(
            [new(root.FullName, SkillScope.User, true)],
            SkillConfiguration.Default);

        _ = await Assert.That(snapshot.Skills).IsEmpty();
        _ = await Assert.That(snapshot.Errors.Count).IsEqualTo(2);
        _ = await Assert.That(snapshot.Errors.Any(error => error.Message.Contains("1 MiB", StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(snapshot.Errors.Any(error => error.Message.Contains("UTF-8", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Global_disable_keeps_management_listing_but_selection_omits_skill(CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "globally-disabled"));
        var path = await WriteSkill(root.FullName, "skill", "skill", cancellationToken);

        var snapshot = SkillDiscovery.Discover(
            [new(root.FullName, SkillScope.User, true)],
            new SkillConfiguration(false, []));
        var selected = SkillSelection.Select(snapshot.Skills, SkillMentionParser.Parse("$skill"));

        _ = await Assert.That(snapshot.Skills.Count).IsEqualTo(1);
        _ = await Assert.That(snapshot.Skills[0].Path).IsEqualTo(SecurityWriteTarget.Resolve(path).Path);
        _ = await Assert.That(snapshot.Skills[0].Enabled).IsFalse();
        _ = await Assert.That(selected).IsEmpty();
    }

    [Test]
    public async Task Prompt_products_share_one_catalog(CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "prompt-skills"));
        _ = await WriteSkill(root.FullName, "skill", "skill", cancellationToken);
        var catalog = new SkillCatalog(
            [new(root.FullName, SkillScope.User, true)],
            () => (SkillConfiguration.Default, 0L));
        var provider = new AgentSkillPromptProvider(new AgentSkills(catalog, TestModels.PromptTemplates));
        var first = provider.Materialize(AgentIdentity.Main("first", "first", TestModels.PromptTemplates));
        var second = provider.Materialize(AgentIdentity.Main("second", "second", TestModels.PromptTemplates));
        var modelProvider = new UnusedProvider();
        var model = new ProviderModel(modelProvider, new LLMModel("model", modelProvider.Id));
        var selection = new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            TestModels.Profile().SecurityProfile);

        var firstPrompt = first.Build(selection);
        var secondPrompt = second.Build(selection);

        _ = await Assert.That(firstPrompt).Contains("$skill");
        _ = await Assert.That(secondPrompt).IsEqualTo(firstPrompt);
    }

    [Test]
    public async Task Catalog_returns_same_snapshot_until_invalidated_and_refreshes_configuration(CancellationToken cancellationToken)
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "skills"));
        var path = await WriteSkill(root.FullName, "skill", "skill", cancellationToken);
        var enabled = SkillConfiguration.Default;
        var catalog = new SkillCatalog(
            [new(root.FullName, SkillScope.User, true)],
            () => (enabled, 0L));
        var first = catalog.Capture();
        enabled = new(false, [new(path, false)]);
        var stable = catalog.Capture();
        catalog.Invalidate();
        var refreshed = catalog.Capture();

        _ = await Assert.That(ReferenceEquals(first, stable)).IsTrue();
        _ = await Assert.That(refreshed.Skills.Count).IsEqualTo(1);
        _ = await Assert.That(refreshed.Skills[0].Enabled).IsFalse();
    }

    private static string SkillContent(string name) => $"---\nname: {name}\ndescription: description\n---\nbody";

    private static async Task<string> WriteSkill(
        string root,
        string directory,
        string name,
        CancellationToken cancellationToken)
    {
        var skillDirectory = Directory.CreateDirectory(Path.Combine(root, directory));
        var path = Path.Combine(skillDirectory.FullName, "SKILL.md");
        await File.WriteAllTextAsync(path, SkillContent(name), cancellationToken);
        return Path.GetFullPath(path);
    }

    private SkillCatalogFactory Factory(string userHome, string packagedRoot)
    {
        var configurationRoot = Directory.CreateDirectory(Path.Combine(_root, "configuration"));
        var configuration = Configuration.Load(
            Path.Combine(configurationRoot.FullName, "config.yaml"),
            Path.Combine(configurationRoot.FullName, "predefined.yaml"));
        return new(configuration, userHome, packagedRoot);
    }
}
