using System.Text.Json;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.State;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ProtectedFilesystemToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-protected-filesystem-tests", Guid.NewGuid().ToString("n"));

    private readonly string _workspace;
    private readonly StatePaths _paths;
    private readonly ToolWorkspace _toolWorkspace;
    private readonly SecurityProfile _permissive;

    public ProtectedFilesystemToolTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _paths = new StatePaths(
            Path.Combine(_workspace, "private-state"),
            Path.Combine(_workspace, "private-config"),
            Path.Combine(_workspace, "private-data"));
        _ = Directory.CreateDirectory(_workspace);
        _ = Directory.CreateDirectory(_paths.State);
        _ = Directory.CreateDirectory(_paths.Config);
        _ = Directory.CreateDirectory(_paths.Data);
        _toolWorkspace = new ToolWorkspace(_workspace, new ToolFileSystemPolicy(_paths));
        _permissive = SecurityProfile.Compose(
            readOnly: false,
            [],
            [],
            [new SandboxRule(_workspace, SandboxRuleAction.AllowWrite)]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [Arguments("private-state")]
    [Arguments("private-config")]
    [Arguments("private-data")]
    public async Task Read_denies_every_mandatory_private_root(
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_workspace, directory, "secret.txt");
        await File.WriteAllTextAsync(path, "secret", cancellationToken);

        var result = await new ReadTool(_toolWorkspace, _permissive).Execute(
            string.Concat("{\"path\":\"", directory, "/secret.txt\"}"),
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: Access to protected application data is denied.");
    }

    [Test]
    public async Task Recursive_read_tools_hide_protected_subtrees(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "visible.txt"), "visible", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_paths.State, "secret.txt"), "secret", cancellationToken);
        var alias = Path.Combine(_workspace, "private-alias");
        _ = Directory.CreateSymbolicLink(alias, _paths.State);

        var listing = await new ReadTool(_toolWorkspace, _permissive).Execute(
            "{\"path\":\".\"}",
            cancellationToken);
        var glob = await new GlobTool(_toolWorkspace, _permissive).Execute(
            "{\"pattern\":\"**\"}",
            cancellationToken);
        var grep = await new GrepTool(_toolWorkspace, _permissive).Execute(
            "{\"pattern\":\"secret\"}",
            cancellationToken);

        _ = await Assert.That(listing).Contains("visible.txt");
        _ = await Assert.That(listing).DoesNotContain("private-state");
        _ = await Assert.That(listing).DoesNotContain("private-alias");
        _ = await Assert.That(glob).Contains("visible.txt");
        _ = await Assert.That(glob).DoesNotContain("private-state");
        _ = await Assert.That(glob).DoesNotContain("private-alias");
        _ = await Assert.That(grep).IsEmpty();
    }

    [Test]
    public async Task Read_denies_a_symlink_targeting_a_protected_root(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.State, "secret.txt"), "secret", cancellationToken);
        _ = Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _paths.State);

        var result = await new ReadTool(_toolWorkspace, _permissive).Execute(
            "{\"path\":\"alias/secret.txt\"}",
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: Access to protected application data is denied.");
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Mutations_cannot_reopen_protected_roots(
        string toolName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_paths.State, "claim.txt");
        await File.WriteAllTextAsync(path, "old", cancellationToken);
        var profile = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new SandboxRule(_paths.State, SandboxRuleAction.AllowWrite)]);
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(path));
        ITool tool = toolName switch
        {
            "write" => new WriteTool(_toolWorkspace, profile, grants),
            "edit" => new EditTool(_toolWorkspace, profile, grants),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };
        var arguments = toolName switch
        {
            "write" => "{\"path\":\"private-state/claim.txt\",\"content\":\"new\"}",
            "edit" => "{\"path\":\"private-state/claim.txt\",\"old_string\":\"old\",\"new_string\":\"new\",\"replace_all\":false}",
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };

        var result = await tool.Execute(arguments, cancellationToken);

        _ = await Assert.That(result).Contains("Access to protected application data is denied.");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("old");
    }

    [Test]
    public async Task A_workspace_inside_a_protected_root_is_denied(CancellationToken cancellationToken)
    {
        var nested = Directory.CreateDirectory(Path.Combine(_paths.State, "nested-workspace")).FullName;
        var workspace = new ToolWorkspace(nested, new ToolFileSystemPolicy(_paths));
        await File.WriteAllTextAsync(Path.Combine(nested, "secret.txt"), "secret", cancellationToken);

        var read = await new ReadTool(workspace, _permissive).Execute(
            "{\"path\":\"secret.txt\"}",
            cancellationToken);
        var glob = await new GlobTool(workspace, _permissive).Execute(
            "{\"pattern\":\"**\"}",
            cancellationToken);
        var grep = await new GrepTool(workspace, _permissive).Execute(
            "{\"pattern\":\"secret\"}",
            cancellationToken);

        _ = await Assert.That(read).Contains("protected application data");
        _ = await Assert.That(glob).Contains("protected application data");
        _ = await Assert.That(grep).Contains("protected application data");
    }

    [Test]
    [Arguments("read")]
    [Arguments("grep")]
    [Arguments("glob")]
    public async Task Explicit_read_allow_cannot_reopen_a_protected_root(
        string toolName,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.State, "secret.txt"), "secret", cancellationToken);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [],
            [new SandboxRule(_paths.State, SandboxRuleAction.AllowRead)],
            []);
        ITool tool = toolName switch
        {
            "read" => new ReadTool(_toolWorkspace, profile),
            "grep" => new GrepTool(_toolWorkspace, profile),
            "glob" => new GlobTool(_toolWorkspace, profile),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };
        var arguments = toolName switch
        {
            "read" => FormatPathArguments(_paths.State),
            "grep" => FormatSearchArguments("secret", _paths.State),
            "glob" => FormatGlobArguments("**", _paths.State),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };

        var result = await tool.Execute(arguments, cancellationToken);

        _ = await Assert.That(result).Contains("protected application data");
    }

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
