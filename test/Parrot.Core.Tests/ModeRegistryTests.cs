using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class ModeRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Registry_defaults_lists_and_rejects_unknown_modes()
    {
        var registry = Registry();

        _ = await Assert.That(registry.Resolve(string.Empty, "session").Id).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(string.Join(" | ", registry.List())).IsEqualTo("build | plan | query");
        _ = await Assert.That(() => registry.Resolve("child", "session")).Throws<ModeRegistryException>();
    }

    [Test]
    public async Task Plan_prepare_creates_private_artifact_and_truncates_existing_content()
    {
        var profile = Registry().Resolve(ModeRegistry.Plan, "session");

        profile.Prepare();
        await File.WriteAllTextAsync(profile.PlanArtifact, "stale plan");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(profile.PlanArtifact, UnixFileMode.OtherRead | UnixFileMode.GroupRead);
        }

        profile.Prepare();

        _ = await Assert.That(File.Exists(profile.PlanArtifact)).IsTrue();
        _ = await Assert.That(new FileInfo(profile.PlanArtifact).Length).IsEqualTo(0L);

        if (!OperatingSystem.IsWindows())
        {
            _ = await Assert.That(File.GetUnixFileMode(profile.PlanArtifact))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    [Arguments(ModeRegistry.Build, false, 64, "build mode", "authorized workspace")]
    [Arguments(ModeRegistry.Plan, true, 24, "plan mode", "only writable path")]
    [Arguments(ModeRegistry.Query, true, 24, "query mode", "Read-only mode")]
    public async Task Foreground_modes_expose_their_policy(
        string id,
        bool readOnly,
        int maxToolRounds,
        string promptFragment,
        string ruleFragment)
    {
        var profile = Registry().Resolve(id, "session");

        _ = await Assert.That(profile.Id).IsEqualTo(id);
        _ = await Assert.That(profile.ReadOnly).IsEqualTo(readOnly);
        _ = await Assert.That(profile.MaxToolRounds).IsEqualTo(maxToolRounds);
        _ = await Assert.That(profile.Prompt).Contains(promptFragment);
        _ = await Assert.That(profile.HardRule).Contains(ruleFragment);
        _ = await Assert.That(profile.Status).IsNotEmpty();
        _ = await Assert.That(profile.PlanArtifact.Length > 0).IsEqualTo(id == ModeRegistry.Plan);
    }

    private ModeRegistry Registry() => new(Path.Combine(_root, "plans"));
}
