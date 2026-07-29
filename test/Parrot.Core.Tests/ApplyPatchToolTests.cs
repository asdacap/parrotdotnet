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

        _ = await Assert.That(result).IsEqualTo(
            "error: patch planning failed with 1 errors:\n1. update 'other.md': Write access denied for 'other.md'.");
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

        _ = await Assert.That(denied).IsEqualTo(
            "error: patch planning failed with 1 errors:\n1. update 'plan.md': Write access denied for 'plan.md'.");
        _ = await Assert.That(await File.ReadAllTextAsync(physicalFile, cancellationToken)).IsEqualTo("old\n");

        var physicalProfile = SecurityProfile.Compose(
            readOnly: true,
            [],
            [],
            [new SandboxRule(physicalFile, SandboxRuleAction.AllowWrite)]);

        var allowed = await new ApplyPatchTool(linkedRoot, physicalProfile).Execute(patch, cancellationToken);

        _ = await Assert.That(allowed).IsEqualTo(
            "Chunk 1 has 1 match.\n\n--- a/plan.md\n+++ b/plan.md\n@@ -1,1 +1,1 @@\n-old\n+new\n");
        _ = await Assert.That(await File.ReadAllTextAsync(physicalFile, cancellationToken)).IsEqualTo("new\n");
    }

    [Test]
    public async Task Execute_returns_focused_unified_diff_for_an_update(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(
            path,
            "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\n",
            cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"file.txt\n<<<<<<< SEARCH\nline 4\n=======\nchanged\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo(
            "Chunk 1 has 1 match.\n\n--- a/file.txt\n+++ b/file.txt\n@@ -1,7 +1,7 @@\n line 1\n line 2\n line 3\n-line 4\n+changed\n line 5\n line 6\n line 7\n");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).Contains("changed\n");
    }

    [Test]
    public async Task Execute_returns_creation_and_deletion_diffs(CancellationToken cancellationToken)
    {
        var deleted = Path.Combine(_root, "deleted.txt");
        await File.WriteAllTextAsync(deleted, "remove\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var created = await tool.Execute(
            """
            {"format":"unified","patchText":"--- /dev/null\n+++ b/created.txt\n@@ -0,0 +1,1 @@\n+created\n"}
            """,
            cancellationToken);
        var removed = await tool.Execute(
            """
            {"format":"unified","patchText":"--- a/deleted.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-remove\n"}
            """,
            cancellationToken);

        _ = await Assert.That(created).IsEqualTo(
            "--- /dev/null\n+++ b/created.txt\n@@ -0,0 +1,1 @@\n+created\n");
        _ = await Assert.That(removed).IsEqualTo(
            "--- a/deleted.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-remove\n");
    }

    [Test]
    public async Task Execute_returns_diffs_in_patch_operation_order(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "before\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"first.txt\n<<<<<<< SEARCH\nbefore\n=======\nafter\n>>>>>>> REPLACE\nsecond.txt\n<<<<<<< SEARCH\n=======\ncreated\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo(
            "Chunk 1 has 1 match.\n\n--- a/first.txt\n+++ b/first.txt\n@@ -1,1 +1,1 @@\n-before\n+after\n--- /dev/null\n+++ b/second.txt\n@@ -0,0 +1,1 @@\n+created\n");
    }

    [Test]
    public async Task Execute_replaces_every_nonoverlapping_aider_match(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "repeated.txt");
        await File.WriteAllTextAsync(path, "old\nkeep\nold\nold\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"repeated.txt\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).StartsWith("Chunk 1 has 3 matches.\n\n--- a/repeated.txt\n");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken))
            .IsEqualTo("new\nkeep\nnew\nnew\n");
    }

    [Test]
    public async Task Execute_numbers_aider_chunks_across_files(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "one\none\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "second.txt"), "two\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"first.txt\n<<<<<<< SEARCH\none\n=======\nchanged\n>>>>>>> REPLACE\nsecond.txt\n<<<<<<< SEARCH\ntwo\n=======\nchanged\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).StartsWith(
            "Chunk 1 has 2 matches.\nChunk 2 has 1 match.\n\n--- a/first.txt\n");
    }

    [Test]
    public async Task Execute_reports_interleaved_aider_paths_in_input_order(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "first.txt"), "one\none\nthree\nthree\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "second.txt"), "two\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"first.txt\n<<<<<<< SEARCH\none\n=======\nchanged\n>>>>>>> REPLACE\nsecond.txt\n<<<<<<< SEARCH\ntwo\n=======\nchanged\n>>>>>>> REPLACE\nfirst.txt\n<<<<<<< SEARCH\nthree\n=======\nchanged\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).StartsWith(
            "Chunk 1 has 2 matches.\nChunk 2 has 1 match.\nChunk 3 has 2 matches.\n\n--- a/first.txt\n");
    }

    [Test]
    public async Task Execute_uses_the_first_successful_aider_comparison(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "spacing.txt");
        await File.WriteAllTextAsync(path, "old\n old \nold\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"spacing.txt\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).StartsWith("Chunk 1 has 2 matches.\n");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken))
            .IsEqualTo("new\n old \nnew\n");
    }

    [Test]
    public async Task Execute_does_not_apply_overlapping_aider_matches(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "overlap.txt");
        await File.WriteAllTextAsync(path, "a\na\na\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"overlap.txt\n<<<<<<< SEARCH\na\na\n=======\nb\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).StartsWith("Chunk 1 has 1 match.\n");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("b\na\n");
    }

    [Test]
    public async Task Execute_preserves_each_aider_match_line_ending_and_final_line(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "repeated-mixed.txt");
        await File.WriteAllBytesAsync(
            path,
            [0xef, 0xbb, 0xbf, (byte)'x', (byte)'\r', (byte)'\n', (byte)'x', (byte)'\r', (byte)'x'],
            cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"repeated-mixed.txt\n<<<<<<< SEARCH\nx\n=======\ny\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).StartsWith("Chunk 1 has 3 matches.\n");
        var actual = await File.ReadAllBytesAsync(path, cancellationToken);
        _ = await Assert.That(actual.AsSpan().SequenceEqual(
            new byte[] { 0xef, 0xbb, 0xbf, (byte)'y', (byte)'\r', (byte)'\n', (byte)'y', (byte)'\r', (byte)'y' })).IsTrue();
    }

    [Test]
    public async Task Execute_rejects_a_missing_search_without_writing(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "missing.txt");
        await File.WriteAllTextAsync(path, "before\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"missing.txt\n<<<<<<< SEARCH\nabsent\n=======\nafter\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo(
            "error: patch planning failed with 1 errors:\n1. update 'missing.txt': hunk 1: Failed to find expected lines 'absent'.");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("before\n");
    }

    [Test]
    public async Task Execute_rejects_duplicate_unified_matches_without_writing(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "unified.txt");
        await File.WriteAllTextAsync(path, "old\nold\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"format":"unified","patchText":"--- a/unified.txt\n+++ b/unified.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n"}
            """,
            cancellationToken);

        _ = await Assert.That(result).Contains("Found 2 matches for 'old'; include more surrounding lines.");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("old\nold\n");
    }

    [Test]
    public async Task Execute_keeps_successful_unified_updates_diff_only(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "unified-unique.txt");
        await File.WriteAllTextAsync(path, "old\n", cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"format":"unified","patchText":"--- a/unified-unique.txt\n+++ b/unified-unique.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n"}
            """,
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo(
            "--- a/unified-unique.txt\n+++ b/unified-unique.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n");
    }

    [Test]
    public async Task Execute_preserves_bom_and_mixed_line_terminators(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, "mixed.txt");
        await File.WriteAllBytesAsync(path, [0xef, 0xbb, 0xbf, (byte)'a', (byte)'\r', (byte)'\n', (byte)'b', (byte)'\r', (byte)'c'], cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        _ = await tool.Execute(
            """
            {"patchText":"mixed.txt\n<<<<<<< SEARCH\nb\n=======\nchanged\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        var actual = await File.ReadAllBytesAsync(path, cancellationToken);
        _ = await Assert.That(actual.AsSpan().SequenceEqual(
            new byte[] { 0xef, 0xbb, 0xbf, (byte)'a', (byte)'\r', (byte)'\n', (byte)'c', (byte)'h', (byte)'a', (byte)'n', (byte)'g', (byte)'e', (byte)'d', (byte)'\r', (byte)'c' })).IsTrue();
    }

    [Test]
    public async Task Execute_allows_an_explicitly_authorized_external_file(CancellationToken cancellationToken)
    {
        var external = Path.Combine(Path.GetTempPath(), $"parrot-external-{Guid.NewGuid():n}.txt");
        await File.WriteAllTextAsync(external, "old\n", cancellationToken);
        try
        {
            var profile = SecurityProfile.Compose(
                readOnly: true,
                [],
                [],
                [new SandboxRule(external, SandboxRuleAction.AllowWrite)]);
            var tool = new ApplyPatchTool(_root, profile);
            var result = await tool.Execute(
                $$"""
                {"patchText":"{{external}}\n<<<<<<< SEARCH\nold\n=======\nnew\n>>>>>>> REPLACE"}
                """,
                cancellationToken);

            _ = await Assert.That(result).Contains(Path.GetRelativePath(_root, external));
            _ = await Assert.That(await File.ReadAllTextAsync(external, cancellationToken)).IsEqualTo("new\n");
        }
        finally
        {
            File.Delete(external);
        }
    }

    [Test]
    public async Task Execute_reports_an_existing_empty_file_as_an_update(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "empty.txt"), string.Empty, cancellationToken);
        var tool = new ApplyPatchTool(_root, SecurityProfile.Compose(false, [], [], []));

        var result = await tool.Execute(
            """
            {"patchText":"empty.txt\n<<<<<<< SEARCH\n=======\nfilled\n>>>>>>> REPLACE"}
            """,
            cancellationToken);

        _ = await Assert.That(result).IsEqualTo(
            "--- a/empty.txt\n+++ b/empty.txt\n@@ -0,0 +1,1 @@\n+filled\n");
    }
}
