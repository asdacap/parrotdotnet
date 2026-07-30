using Parrot.Permissions;

namespace Parrot.Core.Tests;

internal sealed class SandboxWriteGrantsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-write-grant-tests", Guid.NewGuid().ToString("n"));

    public SandboxWriteGrantsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Targets_are_existing_canonical_files_or_directories()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "directory")).FullName;
        var file = Path.Combine(directory, "file");
        await File.WriteAllTextAsync(file, "content");
        var directoryTarget = SandboxWriteTarget.Resolve(directory + Path.DirectorySeparatorChar);
        var fileTarget = SandboxWriteTarget.Resolve(file);

        _ = await Assert.That(directoryTarget.Path).IsEqualTo(directory);
        _ = await Assert.That(directoryTarget.Kind).IsEqualTo(SandboxWriteTargetKind.Directory);
        _ = await Assert.That(fileTarget.Path).IsEqualTo(file);
        _ = await Assert.That(fileTarget.Kind).IsEqualTo(SandboxWriteTargetKind.File);
        _ = await Assert.That(() => SandboxWriteTarget.Resolve("relative"))
            .Throws<ArgumentException>();
        _ = await Assert.That(() => SandboxWriteTarget.Resolve(Path.Combine(_root, "missing")))
            .Throws<FileNotFoundException>();
    }

    [Test]
    public async Task Grants_coalesce_recursively_and_snapshots_are_immutable()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_root, "parent")).FullName;
        var first = Directory.CreateDirectory(Path.Combine(parent, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(parent, "second")).FullName;
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(second));
        grants.Grant(SandboxWriteTarget.Resolve(first));
        var beforeParent = grants.Capture();

        grants.Grant(SandboxWriteTarget.Resolve(parent));
        grants.Grant(SandboxWriteTarget.Resolve(first));
        var afterParent = grants.Capture();

        _ = await Assert.That(string.Join('|', beforeParent.Targets.Select(target => target.Path)))
            .IsEqualTo($"{first}|{second}");
        _ = await Assert.That(string.Join('|', afterParent.Targets.Select(target => target.Path)))
            .IsEqualTo(parent);
        _ = await Assert.That(string.Join('|', beforeParent.Targets.Select(target => target.Path)))
            .IsEqualTo($"{first}|{second}");
    }

    [Test]
    public async Task Snapshot_validation_rejects_replaced_targets()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var approved = Directory.CreateDirectory(Path.Combine(_root, "approved")).FullName;
        var replacement = Directory.CreateDirectory(Path.Combine(_root, "replacement")).FullName;
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(approved));
        var snapshot = grants.Capture();
        Directory.Delete(approved);
        _ = Directory.CreateSymbolicLink(approved, replacement);

        _ = await Assert.That(() => snapshot.Validate(approved))
            .Throws<InvalidOperationException>();
    }
}
