using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class WriteEditToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-write-edit-tests", Guid.NewGuid().ToString("n"));
    private readonly string _externalRoot = Path.Combine(
        Path.GetTempPath(),
        "parrot-write-edit-external-tests",
        Guid.NewGuid().ToString("n"));

    public WriteEditToolTests()
    {
        _ = Directory.CreateDirectory(_root);
        _ = Directory.CreateDirectory(_externalRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        DeleteFiles(_externalRoot);
        DeleteEmptyDirectories(_externalRoot);
    }

    [Test]
    public async Task Tools_expose_the_approved_contracts()
    {
        _ = await Assert.That(WriteTool().ParametersJson).IsEqualTo("""
            {"type":"object","properties":{"path":{"type":"string","minLength":1},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}
            """);
        _ = await Assert.That(EditTool().ParametersJson).IsEqualTo("""
            {"type":"object","properties":{"path":{"type":"string","minLength":1},"old_string":{"type":"string","minLength":1},"new_string":{"type":"string"},"replace_all":{"type":"boolean"}},"required":["path","old_string","new_string","replace_all"],"additionalProperties":false}
            """);
        _ = await Assert.That(WriteTool().Name).IsEqualTo("write");
        _ = await Assert.That(EditTool().Name).IsEqualTo("edit");
    }

    [Test]
    [Arguments("write", "{")]
    [Arguments("write", "{}")]
    [Arguments("write", "{\"path\":1,\"content\":\"x\"}")]
    [Arguments("write", "{\"path\":\"x\",\"content\":false}")]
    [Arguments("write", "{\"path\":\"\",\"content\":\"x\"}")]
    [Arguments("edit", "{")]
    [Arguments("edit", "{}")]
    [Arguments("edit", "{\"path\":\"x\",\"old_string\":\"\",\"new_string\":\"y\",\"replace_all\":false}")]
    [Arguments("edit", "{\"path\":\"x\",\"old_string\":\"x\",\"new_string\":\"y\",\"replace_all\":\"false\"}")]
    public async Task Invalid_runtime_arguments_return_errors(string toolName, string arguments, CancellationToken cancellationToken)
    {
        var result = await Tool(toolName, WritableProfile()).Execute(arguments, cancellationToken);
        _ = await Assert.That(result).StartsWith("error: ");
    }

    [Test]
    [Arguments("write", "null")]
    [Arguments("write", "[]")]
    [Arguments("write", "{\"path\":null,\"content\":\"x\"}")]
    [Arguments("write", "{\"path\":\"untouched.txt\"}")]
    [Arguments("write", "{\"path\":\"untouched.txt\",\"content\":\"x\",\"extra\":true}")]
    [Arguments("edit", "null")]
    [Arguments("edit", "[]")]
    [Arguments("edit", "{\"path\":null,\"old_string\":\"old\",\"new_string\":\"new\",\"replace_all\":false}")]
    [Arguments("edit", "{\"path\":\"untouched.txt\",\"old_string\":\"old\",\"new_string\":\"new\"}")]
    [Arguments("edit", "{\"path\":\"untouched.txt\",\"old_string\":\"old\",\"new_string\":null,\"replace_all\":false}")]
    [Arguments("edit", "{\"path\":\"untouched.txt\",\"old_string\":\"old\",\"new_string\":\"new\",\"replace_all\":false,\"extra\":true}")]
    public async Task Strict_argument_failures_have_no_side_effects(string toolName, string arguments, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "untouched.txt");
        await File.WriteAllTextAsync(path, "old", cancellationToken);
        var result = await Tool(toolName, WritableProfile()).Execute(arguments, cancellationToken);
        _ = await Assert.That(result).StartsWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("old");
        _ = await Assert.That(Directory.GetFileSystemEntries(_root)).HasSingleItem();
    }

    [Test]
    public async Task Empty_write_content_and_edit_replacement_are_valid(CancellationToken cancellationToken)
    {
        var writePath = Path.Combine(_root, "empty.txt");
        var editPath = Path.Combine(_root, "delete.txt");
        await File.WriteAllTextAsync(editPath, "before-middle-after", cancellationToken);
        var written = await WriteTool().Execute(WriteArguments("empty.txt", string.Empty), cancellationToken);
        var edited = await EditTool().Execute(EditArguments("delete.txt", "middle", string.Empty, false), cancellationToken);
        _ = await Assert.That(written).DoesNotStartWith("error: ");
        _ = await Assert.That(edited).DoesNotStartWith("error: ");
        _ = await Assert.That(new FileInfo(writePath).Length).IsEqualTo(0);
        _ = await Assert.That(await File.ReadAllTextAsync(editPath, cancellationToken)).IsEqualTo("before--after");
    }

    [Test]
    public async Task Write_creates_nested_file_verbatim_as_utf8_without_bom_and_with_private_mode(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "nested", "unicode.txt");
        const string content = "héllo\r\n世界\n";
        var result = await WriteTool().Execute(WriteArguments("nested/unicode.txt", content), cancellationToken);
        _ = await Assert.That(result).Contains("+++ b/nested/unicode.txt");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        _ = await Assert.That(bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(content))).IsTrue();
        _ = await Assert.That(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })).IsFalse();
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            _ = await Assert.That(mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _ = await Assert.That(mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)).IsEqualTo((UnixFileMode)0);
        }
    }

    [Test]
    public async Task Write_replaces_existing_file_and_reports_noop(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(path, "before\n", cancellationToken);
        var changed = await WriteTool().Execute(WriteArguments("file.txt", "after\n"), cancellationToken);
        var unchanged = await WriteTool().Execute(WriteArguments("file.txt", "after\n"), cancellationToken);
        _ = await Assert.That(changed).Contains("-before");
        _ = await Assert.That(changed).Contains("+after");
        _ = await Assert.That(unchanged).IsEqualTo("No changes made.");
        _ = await Assert.That((await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("after\n"))).IsTrue();
    }

    [Test]
    public async Task Write_replaces_bom_and_crlf_exactly(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "endings.txt");
        byte[] original = [0xef, 0xbb, 0xbf, (byte)'a', 0x0d, 0x0a, (byte)'b', 0x0d, 0x0a];
        await File.WriteAllBytesAsync(path, original, cancellationToken);
        _ = await WriteTool().Execute(WriteArguments("endings.txt", "a\nb"), cancellationToken);
        _ = await Assert.That((await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan()
            .SequenceEqual(Encoding.UTF8.GetBytes("a\nb"))).IsTrue();
    }

    [Test]
    public async Task Write_noop_preserves_existing_timestamp_and_mode(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "stable.txt");
        await File.WriteAllTextAsync(path, "stable", cancellationToken);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        UnixFileMode mode = default;
        if (!OperatingSystem.IsWindows())
        {
            mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            File.SetUnixFileMode(path, mode);
        }

        var result = await WriteTool().Execute(WriteArguments("stable.txt", "stable"), cancellationToken);
        _ = await Assert.That(result).IsEqualTo("No changes made.");
        _ = await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(timestamp);
        if (!OperatingSystem.IsWindows())
        {
            _ = await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(mode);
        }
    }

    [Test]
    public async Task Edit_requires_one_match_unless_replace_all_is_true(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "matches.txt");
        await File.WriteAllTextAsync(path, "one one", cancellationToken);
        var multiple = await EditTool().Execute(EditArguments("matches.txt", "one", "two", false), cancellationToken);
        var zero = await EditTool().Execute(EditArguments("matches.txt", "missing", "two", false), cancellationToken);
        var allZero = await EditTool().Execute(EditArguments("matches.txt", "missing", "two", true), cancellationToken);
        _ = await Assert.That(multiple).StartsWith("error: ");
        _ = await Assert.That(multiple).Contains("2");
        _ = await Assert.That(zero).StartsWith("error: ");
        _ = await Assert.That(zero).Contains("0");
        _ = await Assert.That(allZero).IsEqualTo("No changes made.");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("one one");
    }

    [Test]
    public async Task Edit_is_ordinal_nonoverlapping_and_identical_output_is_noop(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "ordinal.txt");
        await File.WriteAllTextAsync(path, "aaaa A", cancellationToken);
        var changed = await EditTool().Execute(EditArguments("ordinal.txt", "aa", "b", true), cancellationToken);
        var identical = await EditTool().Execute(EditArguments("ordinal.txt", "bb", "bb", false), cancellationToken);
        _ = await Assert.That(changed).Contains("+bb A");
        _ = await Assert.That(identical).IsEqualTo("No changes made.");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("bb A");
    }

    [Test]
    public async Task Edit_replaces_one_multiline_match_verbatim(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "multiline.txt");
        const string original = "start\r\nfirst\nsecond\rend";
        await File.WriteAllTextAsync(path, original, cancellationToken);
        var result = await EditTool().Execute(
            EditArguments("multiline.txt", "first\nsecond", "ONE\r\nTWO", false),
            cancellationToken);
        _ = await Assert.That(result).DoesNotStartWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken))
            .IsEqualTo("start\r\nONE\r\nTWO\rend");
    }

    [Test]
    public async Task Edit_preserves_bom_untouched_bytes_and_line_endings(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "mixed.txt");
        byte[] original = [0xef, 0xbb, 0xbf, (byte)'a', 0x0d, 0x0a, (byte)'b', 0x0d, (byte)'c', 0x0a];
        await File.WriteAllBytesAsync(path, original, cancellationToken);
        _ = await EditTool().Execute(EditArguments("mixed.txt", "b", "β", false), cancellationToken);
        byte[] expected = [0xef, 0xbb, 0xbf, (byte)'a', 0x0d, 0x0a, 0xce, 0xb2, 0x0d, (byte)'c', 0x0a];
        _ = await Assert.That((await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(new byte[] { 0xff, 0xfe, 0x41 })]
    [Arguments(new byte[] { 0x61, 0x00, 0x62 })]
    public async Task Edit_rejects_invalid_utf8_and_nul_without_modification(byte[] bytes, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "bad.txt");
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var result = await EditTool().Execute(EditArguments("bad.txt", "a", "x", true), cancellationToken);
        _ = await Assert.That(result).StartsWith("error: ");
        _ = await Assert.That((await File.ReadAllBytesAsync(path, cancellationToken)).AsSpan().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Mutations_reject_fifo_targets(string toolName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fifo = Path.Combine(_root, "pipe");
        using var process = System.Diagnostics.Process.Start(new ProcessStartInfo("mkfifo", fifo)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start mkfifo.");
        await process.WaitForExitAsync(cancellationToken);
        _ = await Assert.That(process.ExitCode).IsEqualTo(0);
        var mutation = await Tool(toolName, WritableProfile()).Execute(Arguments(toolName, fifo), cancellationToken);
        _ = await Assert.That(mutation).StartsWith("error: ");
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Mutations_reject_escape_readonly_and_symlink_traversal(string toolName, CancellationToken cancellationToken)
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "file.txt"), "old", cancellationToken);
        _ = Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), target);
        var escape = await Tool(toolName, WritableProfile()).Execute(Arguments(toolName, "../escape.txt"), cancellationToken);
        var denied = await Tool(toolName, SecurityProfile.Compose(true, [], [], [])).Execute(Arguments(toolName, "denied.txt"), cancellationToken);
        var linked = await Tool(toolName, WritableProfile()).Execute(Arguments(toolName, "alias/file.txt"), cancellationToken);
        _ = await Assert.That(escape).StartsWith("error: ");
        _ = await Assert.That(denied).StartsWith("error: ");
        _ = await Assert.That(linked).StartsWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "file.txt"), cancellationToken)).IsEqualTo("old");
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Mutations_reject_nonfiles_file_parents_and_links(string toolName, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "destination"));
        var regularParent = Path.Combine(_root, "regular-parent");
        await File.WriteAllTextAsync(regularParent, "parent", cancellationToken);
        var target = Path.Combine(_root, "target.txt");
        await File.WriteAllTextAsync(target, "old", cancellationToken);
        var fileLink = Path.Combine(_root, "file-link.txt");
        _ = File.CreateSymbolicLink(fileLink, target);
        var danglingTarget = Path.Combine(_root, "missing-target.txt");
        var danglingLink = Path.Combine(_root, "dangling.txt");
        _ = File.CreateSymbolicLink(danglingLink, danglingTarget);

        foreach (var path in new[] { directory.FullName, Path.Combine(regularParent, "child.txt"), fileLink, danglingLink })
        {
            var result = await Tool(toolName, WritableProfile()).Execute(Arguments(toolName, path), cancellationToken);
            _ = await Assert.That(result).StartsWith("error: ");
        }

        _ = await Assert.That(await File.ReadAllTextAsync(target, cancellationToken)).IsEqualTo("old");
        _ = await Assert.That(File.Exists(danglingTarget)).IsFalse();
    }

    [Test]
    public async Task Write_allows_external_new_file_until_later_deny(CancellationToken cancellationToken)
    {
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_externalRoot, "write-external"));
        var external = Path.Combine(externalDirectory.FullName, "new.txt");
        try
        {
            var allowed = SecurityProfile.Compose(
                true,
                [],
                [],
                [new SandboxRule(externalDirectory.FullName, SandboxRuleAction.AllowWrite)]);
            var created = await Tool("write", allowed).Execute(WriteArguments(external, "new"), cancellationToken);
            _ = await Assert.That(created).DoesNotStartWith("error: ");

            var denied = SecurityProfile.Compose(
                true,
                [],
                [],
                [
                    new SandboxRule(externalDirectory.FullName, SandboxRuleAction.AllowWrite),
                    new SandboxRule(external, SandboxRuleAction.DenyWrite),
                ]);
            var result = await Tool("write", denied).Execute(WriteArguments(external, "changed"), cancellationToken);
            _ = await Assert.That(result).StartsWith("error: ");
            _ = await Assert.That(await File.ReadAllTextAsync(external, cancellationToken)).IsEqualTo("new");
        }
        finally
        {
            File.Delete(external);
        }
    }

    [Test]
    public async Task Mutations_support_a_symlinked_workspace_root(CancellationToken cancellationToken)
    {
        var physical = Directory.CreateDirectory(Path.Combine(_root, "physical"));
        var alias = Path.Combine(_root, "workspace-link");
        _ = Directory.CreateSymbolicLink(alias, physical.FullName);
        var workspace = new ToolWorkspace(alias);
        var write = new WriteTool(workspace, WritableProfile(), new SandboxWriteGrants());
        var edit = new EditTool(workspace, WritableProfile(), new SandboxWriteGrants());

        var written = await write.Execute(WriteArguments("file.txt", "old"), cancellationToken);
        var edited = await edit.Execute(EditArguments("file.txt", "old", "new", false), cancellationToken);

        _ = await Assert.That(written).DoesNotStartWith("error: ");
        _ = await Assert.That(edited).DoesNotStartWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(physical.FullName, "file.txt"), cancellationToken))
            .IsEqualTo("new");
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Mutations_allow_authorized_absolute_external_paths(string toolName, CancellationToken cancellationToken)
    {
        var external = Path.Combine(_externalRoot, $"authorized-{toolName}.txt");
        await File.WriteAllTextAsync(external, "old", cancellationToken);
        try
        {
            var profile = SecurityProfile.Compose(true, [], [], [new SandboxRule(external, SandboxRuleAction.AllowWrite)]);
            var result = await Tool(toolName, profile).Execute(Arguments(toolName, external), cancellationToken);
            _ = await Assert.That(result).DoesNotStartWith("error: ");
            _ = await Assert.That(await File.ReadAllTextAsync(external, cancellationToken)).IsEqualTo("new");
        }
        finally
        {
            File.Delete(external);
        }
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Existing_file_grants_authorize_only_the_approved_file(
        string toolName,
        CancellationToken cancellationToken)
    {
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_externalRoot, $"file-grant-{toolName}"));
        var granted = Path.Combine(externalDirectory.FullName, "granted.txt");
        var denied = Path.Combine(externalDirectory.FullName, "denied.txt");
        await File.WriteAllTextAsync(granted, "old", cancellationToken);
        await File.WriteAllTextAsync(denied, "old", cancellationToken);
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(granted));
        var profile = SecurityProfile.Compose(
            false,
            [new SandboxRule(_root, SandboxRuleAction.AllowRead)],
            [],
            []);
        var tool = Tool(toolName, profile, grants);

        var allowedResult = await tool.Execute(Arguments(toolName, granted), cancellationToken);
        var deniedResult = await tool.Execute(Arguments(toolName, denied), cancellationToken);

        _ = await Assert.That(allowedResult).DoesNotStartWith("error: ");
        _ = await Assert.That(deniedResult).StartsWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(granted, cancellationToken)).IsEqualTo("new");
        _ = await Assert.That(await File.ReadAllTextAsync(denied, cancellationToken)).IsEqualTo("old");
        File.Delete(granted);
        File.Delete(denied);
    }

    [Test]
    public async Task Directory_grants_authorize_descendant_edits_and_create_paths(CancellationToken cancellationToken)
    {
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_externalRoot, "directory-grant"));
        var granted = externalDirectory.FullName;
        var existing = Path.Combine(granted, "existing.txt");
        await File.WriteAllTextAsync(existing, "old", cancellationToken);
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(granted));
        var profile = SecurityProfile.Compose(
            false,
            [new SandboxRule(_root, SandboxRuleAction.AllowRead)],
            [],
            []);

        var edited = await Tool("edit", profile, grants).Execute(
            EditArguments(existing, "old", "new", false),
            cancellationToken);
        var createdDirectory = Path.Combine(granted, "nested");
        var createdPath = Path.Combine(createdDirectory, "created.txt");
        var written = await Tool("write", profile, grants).Execute(
            WriteArguments(createdPath, "created"),
            cancellationToken);

        _ = await Assert.That(edited).DoesNotStartWith("error: ");
        _ = await Assert.That(written).DoesNotStartWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(existing, cancellationToken)).IsEqualTo("new");
        _ = await Assert.That(await File.ReadAllTextAsync(createdPath, cancellationToken)).IsEqualTo("created");
        File.Delete(existing);
        File.Delete(createdPath);
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Stale_grants_do_not_block_unrelated_workspace_mutations(
        string toolName,
        CancellationToken cancellationToken)
    {
        var granted = Path.Combine(_root, "stale-grant.txt");
        await File.WriteAllTextAsync(granted, "old", cancellationToken);
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(granted));
        File.Delete(granted);
        var workspacePath = Path.Combine(_root, "workspace.txt");
        await File.WriteAllTextAsync(workspacePath, "old", cancellationToken);

        var result = await Tool(toolName, WritableProfile(), grants).Execute(
            Arguments(toolName, workspacePath),
            cancellationToken);

        _ = await Assert.That(result).DoesNotStartWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(workspacePath, cancellationToken)).IsEqualTo("new");
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Read_only_and_static_explicit_deny_override_grants(
        string toolName,
        CancellationToken cancellationToken)
    {
        var externalDirectory = Directory.CreateDirectory(Path.Combine(_externalRoot, $"grant-overrides-{toolName}"));
        var target = Path.Combine(externalDirectory.FullName, "target.txt");
        await File.WriteAllTextAsync(target, "old", cancellationToken);
        var grants = new SandboxWriteGrants();
        grants.Grant(SandboxWriteTarget.Resolve(target));
        var readOnly = SecurityProfile.Compose(true, [], [], []);
        var explicitDeny = SecurityProfile.Compose(
            false,
            [new SandboxRule(target, SandboxRuleAction.DenyWrite)],
            [],
            []);

        var readOnlyResult = await Tool(toolName, readOnly, grants).Execute(
            Arguments(toolName, target),
            cancellationToken);
        var deniedResult = await Tool(toolName, explicitDeny, grants).Execute(
            Arguments(toolName, target),
            cancellationToken);

        _ = await Assert.That(readOnlyResult).StartsWith("error: ");
        _ = await Assert.That(deniedResult).StartsWith("error: ");
        _ = await Assert.That(await File.ReadAllTextAsync(target, cancellationToken)).IsEqualTo("old");
        File.Delete(target);
    }

    [Test]
    [Arguments("write")]
    [Arguments("edit")]
    public async Task Mutations_observe_precancelled_tokens(string toolName)
    {
        if (toolName == "edit")
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "cancel.txt"), "old");
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _ = await Assert.That(async () => await Tool(toolName, WritableProfile()).Execute(Arguments(toolName, "cancel.txt"), cancellation.Token)).Throws<OperationCanceledException>();
    }

    private static SecurityProfile WritableProfile() => SecurityProfile.Compose(false, [], [], []);

    private static void DeleteFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderDescending())
        {
            try
            {
                Directory.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            Directory.Delete(root);
        }
        catch (IOException)
        {
        }
    }

    private static string Arguments(string name, string path) => name == "write" ? WriteArguments(path, "new") : EditArguments(path, "old", "new", false);

    private static string WriteArguments(string path, string content) => $"{{\"path\":\"{Encode(path)}\",\"content\":\"{Encode(content)}\"}}";

    private static string EditArguments(string path, string oldString, string newString, bool replaceAll) => $"{{\"path\":\"{Encode(path)}\",\"old_string\":\"{Encode(oldString)}\",\"new_string\":\"{Encode(newString)}\",\"replace_all\":{replaceAll.ToString().ToLowerInvariant()}}}";

    private static string Encode(string value) => JsonEncodedText.Encode(value).ToString();

    private WriteTool WriteTool() =>
        new(new ToolWorkspace(_root), WritableProfile(), new SandboxWriteGrants());

    private EditTool EditTool() =>
        new(new ToolWorkspace(_root), WritableProfile(), new SandboxWriteGrants());

    private ITool Tool(string name, SecurityProfile profile) =>
        Tool(name, profile, new SandboxWriteGrants());

    private ITool Tool(string name, SecurityProfile profile, SandboxWriteGrants writeGrants)
    {
        var workspace = new ToolWorkspace(_root);
        return name == "write"
            ? new WriteTool(workspace, profile, writeGrants)
            : new EditTool(workspace, profile, writeGrants);
    }
}
