using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class IdentityStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("../other")]
    [Arguments("other/session")]
    [Arguments("other\\session")]
    [Arguments("session\u0001")]
    [Arguments("/rooted")]
    public async Task User_session_ids_reject_unsafe_values(string value)
    {
        _ = await Assert.That(UserSessionId.TryParse(value, out _)).IsFalse();
        _ = await Assert.That(() => UserSessionId.Parse(value)).Throws<FormatException>();
    }

    [Test]
    public async Task Generated_and_safe_legacy_ids_are_valid()
    {
        var generated = UserSessionId.Generate();
        var legacy = UserSessionId.Parse("legacy_session-17.alpha");

        _ = await Assert.That(UserSessionId.Parse(generated.Value)).IsEqualTo(generated);
        _ = await Assert.That(legacy.Value).IsEqualTo("legacy_session-17.alpha");
    }

    [Test]
    public async Task Workspace_keeps_the_exact_launch_path_and_compares_physical_identity()
    {
        var physical = Directory.CreateDirectory(Path.Combine(_root, "physical")).FullName;
        var launch = physical + Path.DirectorySeparatorChar;
        var workspace = ProjectWorkspace.FromLaunchDirectory(launch);

        _ = await Assert.That(workspace.LaunchDirectory).IsEqualTo(launch);
        _ = await Assert.That(workspace.PhysicalIdentity).IsEqualTo(Path.TrimEndingDirectorySeparator(physical));
    }

    [Test]
    public async Task Session_resource_descriptors_are_contained_and_disjoint()
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var paths = Paths();
        var first = new UserSessionResources(
            paths, UserSessionId.Parse("session-one"), ProjectWorkspace.FromLaunchDirectory(workspaceDirectory));
        var second = new UserSessionResources(
            paths, UserSessionId.Parse("session-two"), ProjectWorkspace.FromLaunchDirectory(workspaceDirectory));

        _ = await Assert.That(first.Owns(first.MetadataPath)).IsTrue();
        _ = await Assert.That(first.Owns(first.RuntimeHomeDirectory)).IsTrue();
        _ = await Assert.That(first.Owns(first.CacheDirectory)).IsTrue();
        _ = await Assert.That(first.Owns(first.TemporaryDirectory)).IsTrue();
        _ = await Assert.That(first.Owns(second.Root)).IsFalse();
        _ = await Assert.That(second.Owns(first.Root)).IsFalse();
        _ = await Assert.That(Path.GetDirectoryName(first.CacheDirectory)).IsEqualTo(first.RuntimeDirectory);
        _ = await Assert.That(Path.GetDirectoryName(first.TemporaryDirectory)).IsEqualTo(first.RuntimeDirectory);
    }

    [Test]
    public async Task Owner_bound_index_refuses_metadata_for_another_session()
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var resources = new UserSessionResources(
            Paths(), UserSessionId.Parse("session-one"), ProjectWorkspace.FromLaunchDirectory(workspaceDirectory));
        var index = new SessionIndex(resources);

        _ = await Assert.That(() => index.Publish(Meta("session-two", workspaceDirectory)))
            .Throws<InvalidOperationException>();
        _ = await Assert.That(Directory.Exists(resources.Root)).IsFalse();
    }

    [Test]
    public async Task Catalog_isolates_corrupt_entries_and_omits_unsafe_directory_names()
    {
        var paths = Paths();
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var resources = new UserSessionResources(
            paths, UserSessionId.Parse("session-valid"), ProjectWorkspace.FromLaunchDirectory(workspaceDirectory));
        new SessionIndex(resources).Publish(Meta(resources.Id.Value, workspaceDirectory));
        var corrupt = Directory.CreateDirectory(Path.Combine(paths.State, "sessions", "session-corrupt")).FullName;
        await File.WriteAllTextAsync(Path.Combine(corrupt, "meta.json"), "not json");
        _ = Directory.CreateDirectory(Path.Combine(paths.State, "sessions", "..unsafe"));

        var listed = new SessionCatalog(paths).List();

        _ = await Assert.That(listed).Count().IsEqualTo(2);
        _ = await Assert.That(listed.Single(item => item.Id.Value == "session-valid").State)
            .IsEqualTo(SessionCatalogState.Inactive);
        var invalid = listed.Single(item => item.Id.Value == "session-corrupt");
        _ = await Assert.That(invalid.State).IsEqualTo(SessionCatalogState.Corrupt);
        _ = await Assert.That(invalid.WorkingDirectory).IsEmpty();
        _ = await Assert.That(invalid.ProviderId).IsEmpty();
        _ = await Assert.That(invalid.Model).IsEmpty();
    }

    private static SessionMeta Meta(string id, string workingDirectory) =>
        new()
        {
            Id = id,
            WorkingDirectory = workingDirectory,
            ProviderId = "provider",
            Model = "provider/model",
        };

    private StatePaths Paths() =>
        new(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));
}
