using System.Text.Json;
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

        var result = (await tool.Execute(new ToolInvocation("test-call", arguments), cancellationToken)).Text;

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

        var result = (await tool.Execute(new ToolInvocation("test-call", arguments), cancellationToken)).Text;

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

        var listing = (await new ReadTool(workspace, security).Execute(new ToolInvocation("test-call", "{\"path\":\"alias\"}"), cancellationToken)).Text;
        var matches = (await new GrepTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                "{\"pattern\":\"hidden\",\"path\":\"alias\"}"),
            cancellationToken)).Text;

        _ = await Assert.That(listing).Contains("visible.txt");
        _ = await Assert.That(listing).DoesNotContain("hidden.txt");
        _ = await Assert.That(matches).IsEmpty();
    }

    [Test]
    public async Task External_absolute_and_parent_relative_reads_are_allowed_by_default(
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        var externalFile = Path.Combine(externalDirectory, "outside.txt");
        await File.WriteAllTextAsync(externalFile, "outside", cancellationToken);
        var tool = new ReadTool(new ToolWorkspace(workspaceDirectory), Permissive());

        var absolute = (await tool.Execute(new ToolInvocation("test-call", FormatPathArguments(externalFile)), cancellationToken)).Text;
        var parentRelative = (await tool.Execute(
            new ToolInvocation(
                "test-call",
                FormatPathArguments(Path.Combine("..", "external", "outside.txt"))),
            cancellationToken)).Text;

        _ = await Assert.That(absolute).Contains("1: outside");
        _ = await Assert.That(parentRelative).Contains("1: outside");
    }

    [Test]
    public async Task Grep_uses_a_direct_external_file_basename(CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        var externalFile = Path.Combine(externalDirectory, "outside.txt");
        await File.WriteAllTextAsync(externalFile, "external text", cancellationToken);

        var result = (await new GrepTool(new ToolWorkspace(workspaceDirectory), Permissive()).Execute(
            new ToolInvocation(
                "test-call",
                FormatSearchArguments("external", externalFile)),
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("outside.txt:1:external text\n");
    }

    [Test]
    public async Task External_directory_searches_use_paths_relative_to_the_requested_root(
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(externalDirectory, "nested")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "file.txt"), "external text", cancellationToken);
        var workspace = new ToolWorkspace(workspaceDirectory);

        var grep = (await new GrepTool(workspace, Permissive()).Execute(
            new ToolInvocation(
                "test-call",
                FormatSearchArguments("external", externalDirectory)),
            cancellationToken)).Text;
        var glob = (await new GlobTool(workspace, Permissive()).Execute(
            new ToolInvocation(
                "test-call",
                FormatGlobArguments("**", externalDirectory)),
            cancellationToken)).Text;

        _ = await Assert.That(grep).IsEqualTo("nested/file.txt:1:external text\n");
        _ = await Assert.That(glob).IsEqualTo("nested/\nnested/file.txt\n");
    }

    [Test]
    public async Task An_external_symlink_is_readable_when_neither_path_is_denied(
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        await File.WriteAllTextAsync(Path.Combine(externalDirectory, "file.txt"), "external text", cancellationToken);
        var alias = Path.Combine(workspaceDirectory, "alias");
        _ = Directory.CreateSymbolicLink(alias, externalDirectory);
        var workspace = new ToolWorkspace(workspaceDirectory);
        var security = Permissive();

        var read = (await new ReadTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                FormatPathArguments(Path.Combine("alias", "file.txt"))),
            cancellationToken)).Text;
        var grep = (await new GrepTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                FormatSearchArguments("external", Path.Combine("alias", "file.txt"))),
            cancellationToken)).Text;
        var glob = (await new GlobTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                FormatGlobArguments("**", alias)),
            cancellationToken)).Text;

        _ = await Assert.That(read).Contains("1: external text");
        _ = await Assert.That(grep).IsEqualTo("file.txt:1:external text\n");
        _ = await Assert.That(glob).IsEqualTo("file.txt\n");
    }

    [Test]
    [Arguments("read")]
    [Arguments("grep")]
    [Arguments("glob")]
    public async Task A_lexically_denied_external_symlink_root_is_rejected(
        string toolName,
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        await File.WriteAllTextAsync(Path.Combine(externalDirectory, "file.txt"), "external text", cancellationToken);
        var alias = Path.Combine(workspaceDirectory, "alias");
        _ = Directory.CreateSymbolicLink(alias, externalDirectory);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(alias, SandboxRuleAction.DenyRead)],
            []);

        var result = await ExecuteReadTool(toolName, new ToolWorkspace(workspaceDirectory), security, alias, cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: access denied");
    }

    [Test]
    [Arguments("read")]
    [Arguments("grep")]
    [Arguments("glob")]
    public async Task A_physically_denied_external_symlink_root_is_rejected(
        string toolName,
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        await File.WriteAllTextAsync(Path.Combine(externalDirectory, "file.txt"), "external text", cancellationToken);
        var alias = Path.Combine(workspaceDirectory, "alias");
        _ = Directory.CreateSymbolicLink(alias, externalDirectory);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(externalDirectory, SandboxRuleAction.DenyRead)],
            []);

        var result = await ExecuteReadTool(toolName, new ToolWorkspace(workspaceDirectory), security, alias, cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: access denied");
    }

    [Test]
    public async Task External_directory_tools_filter_a_denied_child(CancellationToken cancellationToken)
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_root, "external")).FullName;
        var visible = Path.Combine(externalDirectory, "visible.txt");
        var hidden = Path.Combine(externalDirectory, "hidden.txt");
        await File.WriteAllTextAsync(visible, "matching text", cancellationToken);
        await File.WriteAllTextAsync(hidden, "matching text", cancellationToken);
        var security = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(hidden, SandboxRuleAction.DenyRead)],
            []);
        var workspace = new ToolWorkspace(workspaceDirectory);

        var read = (await new ReadTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                FormatPathArguments(externalDirectory)),
            cancellationToken)).Text;
        var grep = (await new GrepTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                FormatSearchArguments("matching", externalDirectory)),
            cancellationToken)).Text;
        var glob = (await new GlobTool(workspace, security).Execute(
            new ToolInvocation(
                "test-call",
                FormatGlobArguments("**", externalDirectory)),
            cancellationToken)).Text;

        _ = await Assert.That(read).IsEqualTo("visible.txt\n");
        _ = await Assert.That(grep).IsEqualTo("visible.txt:1:matching text\n");
        _ = await Assert.That(glob).IsEqualTo("visible.txt\n");
    }

    private static async Task<string> ExecuteReadTool(
        string toolName,
        ToolWorkspace workspace,
        SecurityProfile security,
        string path,
        CancellationToken cancellationToken) => toolName switch
        {
            "read" => (await new ReadTool(workspace, security).Execute(new ToolInvocation("test-call", FormatPathArguments(path)), cancellationToken)).Text,
            "grep" => (await new GrepTool(workspace, security).Execute(
                new ToolInvocation(
                    "test-call",
                    FormatSearchArguments("external", path)),
                cancellationToken)).Text,
            "glob" => (await new GlobTool(workspace, security).Execute(
                new ToolInvocation(
                    "test-call",
                    FormatGlobArguments("**", path)),
                cancellationToken)).Text,
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };

    private static SecurityProfile Permissive() => SecurityProfile.Compose(false, [], [], []);

    private static string FormatPathArguments(string path) =>
        string.Concat("{\"path\":\"", JsonEncodedText.Encode(path), "\"}");

    private static string FormatSearchArguments(string pattern, string path) =>
        string.Concat(
            "{\"pattern\":\"",
            JsonEncodedText.Encode(pattern),
            "\",\"path\":\"",
            JsonEncodedText.Encode(path),
            "\"}");

    private static string FormatGlobArguments(string pattern, string path) =>
        string.Concat(
            "{\"pattern\":\"",
            JsonEncodedText.Encode(pattern),
            "\",\"path\":\"",
            JsonEncodedText.Encode(path),
            "\"}");
}
