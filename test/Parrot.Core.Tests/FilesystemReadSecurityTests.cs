using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class FilesystemReadSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-filesystem-read-security-tests", Guid.NewGuid().ToString("n"));

    public FilesystemReadSecurityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Arguments("read", "{\"path\":\"private/secret.txt\"}", "error: access denied")]
    [Arguments("read", "{\"path\":\"alias/secret.txt\"}", "error: access denied")]
    [Arguments("glob", "{\"pattern\":\"**\"}", "")]
    [Arguments("grep", "{\"pattern\":\"secret\"}", "")]
    [Arguments("grep", "{\"pattern\":\"secret\",\"path\":\"alias/secret.txt\"}", "error: access denied")]
    public async Task Does_not_disclose_denied_paths(
        string toolName,
        string arguments,
        string expected,
        CancellationToken cancellationToken)
    {
        var privateDirectory = Path.Combine(_root, "private");
        _ = Directory.CreateDirectory(privateDirectory);
        await File.WriteAllTextAsync(Path.Combine(privateDirectory, "secret.txt"), "secret", cancellationToken);
        _ = Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), privateDirectory);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(privateDirectory, SandboxRuleAction.DenyRead)],
            []);
        ITool tool = toolName switch
        {
            "read" => new ReadTool(new ToolWorkspace(_root), security),
            "glob" => new GlobTool(new ToolWorkspace(_root), security),
            "grep" => new GrepTool(new ToolWorkspace(_root), security),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };

        var result = await tool.Execute(arguments, cancellationToken);

        _ = await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("read", "{\"path\":\"alias/visible.txt\"}")]
    [Arguments("glob", "{\"pattern\":\"alias\"}")]
    [Arguments("grep", "{\"pattern\":\"visible\",\"path\":\"alias/visible.txt\"}")]
    public async Task Does_not_disclose_a_denied_symlink_alias(
        string toolName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var publicDirectory = Path.Combine(_root, "public");
        _ = Directory.CreateDirectory(publicDirectory);
        await File.WriteAllTextAsync(Path.Combine(publicDirectory, "visible.txt"), "visible", cancellationToken);
        var alias = Path.Combine(_root, "alias");
        _ = Directory.CreateSymbolicLink(alias, publicDirectory);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(alias, SandboxRuleAction.DenyRead)],
            []);
        ITool tool = toolName switch
        {
            "read" => new ReadTool(new ToolWorkspace(_root), security),
            "glob" => new GlobTool(new ToolWorkspace(_root), security),
            "grep" => new GrepTool(new ToolWorkspace(_root), security),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };

        var result = await tool.Execute(arguments, cancellationToken);

        _ = await Assert.That(result).IsIn(string.Empty, "error: access denied");
    }

    [Test]
    public async Task Directory_aliases_preserve_child_denials(CancellationToken cancellationToken)
    {
        var publicDirectory = Path.Combine(_root, "public");
        _ = Directory.CreateDirectory(publicDirectory);
        await File.WriteAllTextAsync(Path.Combine(publicDirectory, "visible.txt"), "visible", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(publicDirectory, "hidden.txt"), "hidden", cancellationToken);
        var alias = Path.Combine(_root, "alias");
        _ = Directory.CreateSymbolicLink(alias, publicDirectory);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(Path.Combine(alias, "hidden.txt"), SandboxRuleAction.DenyRead)],
            []);
        var workspace = new ToolWorkspace(_root);

        var listing = await new ReadTool(workspace, security).Execute("{\"path\":\"alias\"}", cancellationToken);
        var matches = await new GrepTool(workspace, security).Execute(
            "{\"pattern\":\"hidden\",\"path\":\"alias\"}",
            cancellationToken);

        _ = await Assert.That(listing).Contains("visible.txt");
        _ = await Assert.That(listing).DoesNotContain("hidden.txt");
        _ = await Assert.That(matches).IsEmpty();
    }
}
