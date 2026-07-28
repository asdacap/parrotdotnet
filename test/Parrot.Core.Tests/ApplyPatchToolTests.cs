using Parrot.Security;
using Parrot.Tools.ApplyPatch;

namespace Parrot.Core.Tests;

internal sealed class ApplyPatchToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-apply-patch-tests", Guid.NewGuid().ToString("n"));

    public ApplyPatchToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Preflight_authorizes_every_operation_before_writing_any_file(
        CancellationToken cancellationToken)
    {
        var allowed = Path.Combine(_root, "plan.md");
        var denied = Path.Combine(_root, "other.md");
        await File.WriteAllTextAsync(allowed, "old\n", cancellationToken);
        await File.WriteAllTextAsync(denied, "old\n", cancellationToken);
        var profile = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new SandboxRule(allowed, SandboxRuleAction.AllowWrite)]);
        var tool = new ApplyPatchTool(_root, profile);

        var result = await tool.Execute(
            """
            {"patchText":"plan.md\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE\nother.md\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: Write access denied for 'other.md'.");
        _ = await Assert.That(await File.ReadAllTextAsync(allowed, cancellationToken)).IsEqualTo("old\n");
        _ = await Assert.That(await File.ReadAllTextAsync(denied, cancellationToken)).IsEqualTo("old\n");
    }

    [Test]
    public async Task Preflight_authorizes_the_resolved_destination_of_a_symlinked_working_directory(
        CancellationToken cancellationToken)
    {
        var physicalRoot = Path.Combine(_root, "physical");
        var linkedRoot = Path.Combine(_root, "linked");
        _ = Directory.CreateDirectory(physicalRoot);
        _ = Directory.CreateSymbolicLink(linkedRoot, physicalRoot);
        var physicalFile = Path.Combine(physicalRoot, "plan.md");
        var linkedFile = Path.Combine(linkedRoot, "plan.md");
        await File.WriteAllTextAsync(physicalFile, "old\n", cancellationToken);
        var patch =
            """
            {"patchText":"plan.md\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE"}
            """;
        var lexicalProfile = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new SandboxRule(linkedFile, SandboxRuleAction.AllowWrite)]);

        var denied = await new ApplyPatchTool(linkedRoot, lexicalProfile).Execute(patch, cancellationToken);

        _ = await Assert.That(denied).IsEqualTo("error: Write access denied for 'plan.md'.");
        _ = await Assert.That(await File.ReadAllTextAsync(physicalFile, cancellationToken)).IsEqualTo("old\n");

        var physicalProfile = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new SandboxRule(physicalFile, SandboxRuleAction.AllowWrite)]);

        var allowed = await new ApplyPatchTool(linkedRoot, physicalProfile).Execute(patch, cancellationToken);

        _ = await Assert.That(allowed).IsEqualTo("Applied patch to plan.md");
        _ = await Assert.That(await File.ReadAllTextAsync(physicalFile, cancellationToken)).IsEqualTo("new\n");
    }
}
