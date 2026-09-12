using System.Text.Json;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class CompactionGroupBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-compaction-group-store-tests", Guid.NewGuid().ToString("n"));

    private readonly AgentScratchDirectory _scratch;

    public CompactionGroupBlobStoreTests() => _scratch = new AgentScratchDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Persist_preserves_ordered_message_fields_and_retries_collisions(
        CancellationToken cancellationToken)
    {
        var collision = Path.Combine(_scratch.BlobDirectory, "existing.json");
        await File.WriteAllTextAsync(collision, "existing", cancellationToken);
        var names = new Queue<string>(["existing.json", "group.json"]);
        var store = new CompactionGroupBlobStore(_scratch, names.Dequeue);
        var group = new CompactionGroup(
        [
            LLMMessage.Assistant("ask", [new LLMToolCall("call-1", "first", "{\"a\":1}"), new LLMToolCall("call-2", "second", "raw")]),
            LLMMessage.ToolResult("call-1", "one"),
            LLMMessage.User([LLMContent.ImagePart([0, 1, 255], "image/png"), LLMContent.TextPart("tail")]),
        ],
        3,
        false,
        true);

        var path = await store.Persist(group, cancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        var messages = document.RootElement.GetProperty("messages");

        _ = await Assert.That(path).IsEqualTo(Path.Combine(_scratch.BlobDirectory, "group.json"));
        _ = await Assert.That(messages.GetArrayLength()).IsEqualTo(3);
        _ = await Assert.That(messages[0].GetProperty("role").GetString()).IsEqualTo("Assistant");
        _ = await Assert.That(messages[0].GetProperty("tool_calls")[1].GetProperty("arguments_json").GetString()).IsEqualTo("raw");
        _ = await Assert.That(messages[2].GetProperty("contents")[0].GetProperty("image").GetString()).IsEqualTo("AAH/");
        _ = await Assert.That(await File.ReadAllTextAsync(collision, cancellationToken)).IsEqualTo("existing");
    }

    [Test]
    public async Task Construction_is_lazy_when_secure_persistence_is_unsupported()
    {
        var store = new CompactionGroupBlobStore(
            _scratch,
            static () => "unsupported.json",
            static () => { },
            static () => { },
            CompactionGroupBlobStorePlatform.Unsupported,
            false);
        var group = new CompactionGroup([LLMMessage.User("text")], 1, false, true);

        _ = await Assert.That(async () => await store.Persist(group, CancellationToken.None))
            .Throws<PlatformNotSupportedException>();
        _ = await Assert.That(Directory.GetFiles(_scratch.BlobDirectory)).IsEmpty();
    }

    [Test]
    public async Task Forced_named_fallback_streams_once_and_retries_publish_collisions(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(_scratch.BlobDirectory, "existing.json"), "existing", cancellationToken);
        var names = new Queue<string>(["existing.json", "fallback.json"]);
        var fileCreatedCount = 0;
        var store = new CompactionGroupBlobStore(
            _scratch,
            names.Dequeue,
            static () => { },
            () => fileCreatedCount++,
            CompactionGroupBlobStorePlatform.Linux,
            true);
        var text = new string('x', 8 * 1024 * 1024);
        var group = new CompactionGroup([LLMMessage.User(text)], 1, false, true);

        var path = await store.Persist(group, cancellationToken);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));

        _ = await Assert.That(path).IsEqualTo(Path.Combine(_scratch.BlobDirectory, "fallback.json"));
        _ = await Assert.That(fileCreatedCount).IsEqualTo(1);
        _ = await Assert.That(document.RootElement.GetProperty("messages")[0]
            .GetProperty("contents")[0].GetProperty("text").GetString()).IsEqualTo(text);
        _ = await Assert.That(Directory.GetFiles(_scratch.BlobDirectory, "*.tmp")).IsEmpty();
        _ = await Assert.That(File.GetUnixFileMode(path))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Forced_named_fallback_cancellation_removes_staging_file()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var canceled = new CancellationTokenSource();
        var store = new CompactionGroupBlobStore(
            _scratch,
            static () => "canceled-fallback.json",
            static () => { },
            canceled.Cancel,
            CompactionGroupBlobStorePlatform.Linux,
            true);
        var group = new CompactionGroup([LLMMessage.User(new string('x', 1_000_000))], 1, false, true);

        _ = await Assert.That(async () => await store.Persist(group, canceled.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(Directory.GetFiles(_scratch.BlobDirectory)).IsEmpty();
    }

    [Test]
    public async Task Persist_cancellation_leaves_no_partial_file()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var store = new CompactionGroupBlobStore(_scratch, static () => "canceled.json");
        var group = new CompactionGroup([LLMMessage.User("text")], 1, false, true);

        _ = await Assert.That(async () => await store.Persist(group, canceled.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(File.Exists(Path.Combine(_scratch.BlobDirectory, "canceled.json"))).IsFalse();
    }

    [Test]
    public async Task Persist_rejects_blob_directory_and_ancestor_replacement_before_creation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var group = new CompactionGroup([LLMMessage.User("secret")], 1, false, true);
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        var movedBlobs = _scratch.BlobDirectory + ".moved";
        _ = Directory.CreateDirectory(outside);
        try
        {
            var blobStore = new CompactionGroupBlobStore(
                _scratch,
                static () => "escape.json",
                () =>
                {
                    Directory.Move(_scratch.BlobDirectory, movedBlobs);
                    _ = Directory.CreateSymbolicLink(_scratch.BlobDirectory, outside);
                },
                static () => { },
                CompactionGroupBlobStorePlatform.Linux,
                true);

            _ = await Assert.That(async () => await blobStore.Persist(group, CancellationToken.None))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(File.Exists(Path.Combine(outside, "escape.json"))).IsFalse();
            _ = await Assert.That(File.Exists(Path.Combine(movedBlobs, "escape.json"))).IsFalse();

            Directory.Delete(_scratch.BlobDirectory);
            Directory.Move(movedBlobs, _scratch.BlobDirectory);

            var outsideTree = Path.Combine(outside, "tree");
            _ = Directory.CreateDirectory(Path.Combine(outsideTree, "blobs"));
            var movedRoot = _root + ".moved";
            var ancestorStore = new CompactionGroupBlobStore(
                _scratch,
                static () => "ancestor.json",
                () =>
                {
                    Directory.Move(_root, movedRoot);
                    _ = Directory.CreateSymbolicLink(_root, outsideTree);
                },
                static () => { },
                CompactionGroupBlobStorePlatform.Linux,
                true);

            _ = await Assert.That(async () => await ancestorStore.Persist(group, CancellationToken.None))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(File.Exists(Path.Combine(outsideTree, "blobs", "ancestor.json"))).IsFalse();
            _ = await Assert.That(File.Exists(Path.Combine(movedRoot, "blobs", "ancestor.json"))).IsFalse();

            Directory.Delete(_root);
            Directory.Move(movedRoot, _root);
        }
        finally
        {
            if (Directory.Exists(movedBlobs) && !Directory.Exists(_scratch.BlobDirectory))
            {
                Directory.Move(movedBlobs, _scratch.BlobDirectory);
            }

            var movedRoot = _root + ".moved";
            if (Directory.Exists(movedRoot) && !Directory.Exists(_root))
            {
                Directory.Move(movedRoot, _root);
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task Persist_removes_handle_relative_candidate_when_directory_changes_after_creation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        var movedBlobs = _scratch.BlobDirectory + ".moved";
        _ = Directory.CreateDirectory(outside);
        var outsideArtifact = Path.Combine(outside, "escape.json");
        await File.WriteAllTextAsync(outsideArtifact, "outside");
        try
        {
            var store = new CompactionGroupBlobStore(
                _scratch,
                static () => "escape.json",
                static () => { },
                () =>
                {
                    Directory.Move(_scratch.BlobDirectory, movedBlobs);
                    _ = Directory.CreateSymbolicLink(_scratch.BlobDirectory, outside);
                },
                CompactionGroupBlobStorePlatform.Linux,
                true);
            var group = new CompactionGroup([LLMMessage.User(new string('x', 100_000))], 1, false, true);

            _ = await Assert.That(async () => await store.Persist(group, CancellationToken.None))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(File.Exists(Path.Combine(movedBlobs, "escape.json"))).IsFalse();
            _ = await Assert.That(await File.ReadAllTextAsync(outsideArtifact)).IsEqualTo("outside");
        }
        finally
        {
            if (Directory.Exists(_scratch.BlobDirectory))
            {
                Directory.Delete(_scratch.BlobDirectory, recursive: true);
            }

            if (Directory.Exists(movedBlobs))
            {
                Directory.Move(movedBlobs, _scratch.BlobDirectory);
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Persist_rolls_back_final_link_after_directory_or_ancestor_replacement(
        bool forceNamedFile,
        bool replaceAncestor)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string artifactName = "post-link.json";
        var movedRoot = _root + ".moved";
        var movedBlobs = _scratch.BlobDirectory + ".moved";
        var displacedBlobs = replaceAncestor
            ? Path.Combine(movedRoot, "blobs")
            : movedBlobs;
        try
        {
            var store = new CompactionGroupBlobStore(
                _scratch,
                static () => artifactName,
                static () => { },
                static () => { },
                () =>
                {
                    if (replaceAncestor)
                    {
                        Directory.Move(_root, movedRoot);
                        _ = Directory.CreateDirectory(_scratch.BlobDirectory);
                    }
                    else
                    {
                        Directory.Move(_scratch.BlobDirectory, movedBlobs);
                        _ = Directory.CreateDirectory(_scratch.BlobDirectory);
                    }

                    File.WriteAllText(Path.Combine(_scratch.BlobDirectory, artifactName), "replacement");
                },
                CompactionGroupBlobStorePlatform.Linux,
                forceNamedFile);
            var group = new CompactionGroup([LLMMessage.User("secret payload")], 1, false, true);

            _ = await Assert.That(async () => await store.Persist(group, CancellationToken.None))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(File.Exists(Path.Combine(displacedBlobs, artifactName))).IsFalse();
            _ = await Assert.That(Directory.GetFiles(displacedBlobs, "*.tmp")).IsEmpty();
            _ = await Assert.That(await File.ReadAllTextAsync(
                Path.Combine(_scratch.BlobDirectory, artifactName))).IsEqualTo("replacement");
        }
        finally
        {
            RestoreReplacement(movedRoot, movedBlobs);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Persist_does_not_delete_a_replacement_of_its_final_link(bool forceNamedFile)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string artifactName = "replaced-link.json";
        var movedBlobs = _scratch.BlobDirectory + ".moved";
        try
        {
            var store = new CompactionGroupBlobStore(
                _scratch,
                static () => artifactName,
                static () => { },
                static () => { },
                () =>
                {
                    var artifactPath = Path.Combine(_scratch.BlobDirectory, artifactName);
                    File.Delete(artifactPath);
                    File.WriteAllText(artifactPath, "unrelated");
                    Directory.Move(_scratch.BlobDirectory, movedBlobs);
                    _ = Directory.CreateDirectory(_scratch.BlobDirectory);
                },
                CompactionGroupBlobStorePlatform.Linux,
                forceNamedFile);
            var group = new CompactionGroup([LLMMessage.User("secret payload")], 1, false, true);

            _ = await Assert.That(async () => await store.Persist(group, CancellationToken.None))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(await File.ReadAllTextAsync(
                Path.Combine(movedBlobs, artifactName))).IsEqualTo("unrelated");
            _ = await Assert.That(Directory.GetFiles(movedBlobs, "*.tmp")).IsEmpty();
            _ = await Assert.That(File.Exists(Path.Combine(_scratch.BlobDirectory, artifactName))).IsFalse();
        }
        finally
        {
            RestoreReplacement(_root + ".moved", movedBlobs);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rollback_quarantines_selected_link_before_deleting_it(bool forceNamedFile)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string artifactName = "cleanup-race.json";
        var movedBlobs = _scratch.BlobDirectory + ".moved";
        var destinationRecreationBlocked = false;
        try
        {
            var store = new CompactionGroupBlobStore(
                _scratch,
                static () => artifactName,
                static () => { },
                static () => { },
                () =>
                {
                    var artifactPath = Path.Combine(_scratch.BlobDirectory, artifactName);
                    File.Delete(artifactPath);
                    File.WriteAllText(artifactPath, "unrelated");
                    Directory.Move(_scratch.BlobDirectory, movedBlobs);
                    _ = Directory.CreateDirectory(_scratch.BlobDirectory);
                },
                CompactionGroupBlobStorePlatform.Linux,
                forceNamedFile,
                selectedName =>
                {
                    if (selectedName == artifactName)
                    {
                        try
                        {
                            File.WriteAllText(Path.Combine(movedBlobs, artifactName), "recreated");
                        }
                        catch (UnauthorizedAccessException)
                        {
                            destinationRecreationBlocked = true;
                        }
                    }
                },
                static mask => mask);
            var group = new CompactionGroup([LLMMessage.User("secret payload")], 1, false, true);

            _ = await Assert.That(async () => await store.Persist(group, CancellationToken.None))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(destinationRecreationBlocked).IsTrue();
            _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(movedBlobs, artifactName)))
                .IsEqualTo("unrelated");
            _ = await Assert.That(Directory.GetFiles(movedBlobs, "*.cleanup")).IsEmpty();
            _ = await Assert.That(Directory.GetFiles(movedBlobs, "*.tmp")).IsEmpty();
        }
        finally
        {
            RestoreReplacement(_root + ".moved", movedBlobs);
        }
    }

    [Test]
    public async Task Persist_fails_closed_when_statx_does_not_return_inode_identity()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var store = new CompactionGroupBlobStore(
            _scratch,
            static () => "missing-inode.json",
            static () => { },
            static () => { },
            static () => { },
            CompactionGroupBlobStorePlatform.Linux,
            true,
            static _ => { },
            static mask => mask & ~0x100u);
        var group = new CompactionGroup([LLMMessage.User("secret payload")], 1, false, true);

        _ = await Assert.That(async () => await store.Persist(group, CancellationToken.None))
            .Throws<IOException>();
        _ = await Assert.That(Directory.GetFiles(_scratch.BlobDirectory)).IsEmpty();
    }

    [Test]
    public async Task Persist_creates_owner_only_artifact_where_supported()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var store = new CompactionGroupBlobStore(_scratch, static () => "private.json");
        var group = new CompactionGroup([LLMMessage.User("text")], 1, false, true);

        var path = await store.Persist(group, CancellationToken.None);

        _ = await Assert.That(File.GetUnixFileMode(path))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private void RestoreReplacement(string movedRoot, string movedBlobs)
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(movedRoot))
        {
            Directory.Move(movedRoot, _root);
        }
        else if (Directory.Exists(movedBlobs))
        {
            _ = Directory.CreateDirectory(_root);
            Directory.Move(movedBlobs, _scratch.BlobDirectory);
        }
    }
}
