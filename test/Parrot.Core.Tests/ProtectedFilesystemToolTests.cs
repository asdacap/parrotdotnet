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
    private readonly IReadOnlyList<SandboxRule> _mandatoryRules;
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
        _toolWorkspace = new ToolWorkspace(_workspace);
        _mandatoryRules = new ApplicationDataSecurityRules(_paths).Rules;
        _permissive = SecurityProfile.Compose(
            readOnly: false,
            [],
            [],
            _mandatoryRules,
            []);
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
    public async Task Read_denies_every_mandatory_application_data_root(
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_workspace, directory, "secret.txt");
        await File.WriteAllTextAsync(path, "secret", cancellationToken);

        var result = await new ReadTool(_toolWorkspace, _permissive).Execute(
            string.Concat("{\"path\":\"", directory, "/secret.txt\"}"),
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: access denied");
    }

    [Test]
    public async Task Recursive_read_tools_hide_mandatory_application_data_subtrees(
        CancellationToken cancellationToken)
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
    public async Task Read_denies_a_symlink_targeting_mandatory_application_data(
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.State, "secret.txt"), "secret", cancellationToken);
        _ = Directory.CreateSymbolicLink(Path.Combine(_workspace, "alias"), _paths.State);

        var result = await new ReadTool(_toolWorkspace, _permissive).Execute(
            "{\"path\":\"alias/secret.txt\"}",
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: access denied");
    }

    [Test]
    public async Task A_symlinked_application_data_root_denies_both_its_configured_and_physical_paths(
        CancellationToken cancellationToken)
    {
        var physicalState = Directory.CreateDirectory(Path.Combine(_workspace, "physical-state")).FullName;
        var stateAlias = Path.Combine(_workspace, "state-alias");
        _ = Directory.CreateSymbolicLink(stateAlias, physicalState);
        await File.WriteAllTextAsync(Path.Combine(physicalState, "secret.txt"), "secret", cancellationToken);
        var paths = new StatePaths(stateAlias, _paths.Config, _paths.Data);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [],
            [],
            new ApplicationDataSecurityRules(paths).Rules,
            []);

        var configured = await new ReadTool(_toolWorkspace, profile).Execute(
            FormatPathArguments(Path.Combine(stateAlias, "secret.txt")),
            cancellationToken);
        var physical = await new ReadTool(_toolWorkspace, profile).Execute(
            FormatPathArguments(Path.Combine(physicalState, "secret.txt")),
            cancellationToken);
        var listing = await new ReadTool(_toolWorkspace, profile).Execute("{\"path\":\".\"}", cancellationToken);
        var glob = await new GlobTool(_toolWorkspace, profile).Execute("{\"pattern\":\"**\"}", cancellationToken);
        var grep = await new GrepTool(_toolWorkspace, profile).Execute(
            "{\"pattern\":\"secret\"}",
            cancellationToken);

        _ = await Assert.That(configured).IsEqualTo("error: access denied");
        _ = await Assert.That(physical).IsEqualTo("error: access denied");
        _ = await Assert.That(listing).DoesNotContain("state-alias");
        _ = await Assert.That(listing).DoesNotContain("physical-state");
        _ = await Assert.That(glob).DoesNotContain("state-alias");
        _ = await Assert.That(glob).DoesNotContain("physical-state");
        _ = await Assert.That(grep).IsEmpty();
    }

    [Test]
    [Arguments("write", "private-state")]
    [Arguments("edit", "private-state")]
    [Arguments("write", "private-config")]
    [Arguments("edit", "private-config")]
    [Arguments("write", "private-data")]
    [Arguments("edit", "private-data")]
    public async Task Configured_write_allows_cannot_reopen_mandatory_application_data(
        string toolName,
        string directory,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_workspace, directory);
        var path = Path.Combine(root, "configured.txt");
        await File.WriteAllTextAsync(path, "old", cancellationToken);
        var profile = SecurityProfile.Compose(
            readOnly: true,
            [new SandboxRule(root, SandboxRuleAction.AllowWrite)],
            [],
            _mandatoryRules,
            []);
        var tool = MutationTool(toolName, profile, new SandboxWriteGrants());

        var result = await tool.Execute(MutationArguments(toolName, Path.Combine(directory, "configured.txt")), cancellationToken);

        _ = await Assert.That(result).Contains("Write access denied");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("old");
    }

    [Test]
    [Arguments("write", "private-state")]
    [Arguments("edit", "private-state")]
    [Arguments("write", "private-config")]
    [Arguments("edit", "private-config")]
    [Arguments("write", "private-data")]
    [Arguments("edit", "private-data")]
    public async Task Write_grants_cannot_reopen_mandatory_application_data(
        string toolName,
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_workspace, directory, "granted.txt");
        await File.WriteAllTextAsync(path, "old", cancellationToken);
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(path));
        var tool = MutationTool(toolName, _permissive, grants);

        var result = await tool.Execute(MutationArguments(toolName, Path.Combine(directory, "granted.txt")), cancellationToken);

        _ = await Assert.That(result).Contains("Write access denied");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("old");
    }

    [Test]
    public async Task A_workspace_inside_mandatory_application_data_is_denied(
        CancellationToken cancellationToken)
    {
        var nested = Directory.CreateDirectory(Path.Combine(_paths.State, "nested-workspace")).FullName;
        var workspace = new ToolWorkspace(nested);
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

        _ = await Assert.That(read).IsEqualTo("error: access denied");
        _ = await Assert.That(glob).IsEqualTo("error: access denied");
        _ = await Assert.That(grep).IsEqualTo("error: access denied");
    }

    [Test]
    [Arguments("read", "private-state")]
    [Arguments("grep", "private-state")]
    [Arguments("glob", "private-state")]
    [Arguments("read", "private-config")]
    [Arguments("grep", "private-config")]
    [Arguments("glob", "private-config")]
    [Arguments("read", "private-data")]
    [Arguments("grep", "private-data")]
    [Arguments("glob", "private-data")]
    public async Task Configured_read_allows_cannot_reopen_mandatory_application_data(
        string toolName,
        string directory,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(_workspace, directory);
        await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "secret", cancellationToken);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            [new SandboxRule(root, SandboxRuleAction.AllowRead)],
            [],
            _mandatoryRules,
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
            "read" => FormatPathArguments(root),
            "grep" => FormatSearchArguments("secret", root),
            "glob" => FormatGlobArguments("**", root),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };

        var result = await tool.Execute(arguments, cancellationToken);

        _ = await Assert.That(result).IsEqualTo("error: access denied");
    }

    private static string MutationArguments(string toolName, string path) => toolName switch
    {
        "write" => string.Concat("{\"path\":\"", JsonEncodedText.Encode(path), "\",\"content\":\"new\"}"),
        "edit" => string.Concat(
            "{\"path\":\"",
            JsonEncodedText.Encode(path),
            "\",\"old_string\":\"old\",\"new_string\":\"new\",\"replace_all\":false}"),
        _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
    };

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

    private ITool MutationTool(string toolName, SecurityProfile profile, SandboxWriteGrants grants) =>
        toolName switch
        {
            "write" => new WriteTool(_toolWorkspace, profile, grants),
            "edit" => new EditTool(_toolWorkspace, profile, grants),
            _ => throw new InvalidOperationException($"Unknown tool '{toolName}'."),
        };
}
