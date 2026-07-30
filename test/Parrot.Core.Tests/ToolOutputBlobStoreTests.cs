using System.Text;
using Parrot.Agent;

namespace Parrot.Core.Tests;

internal sealed class ToolOutputBlobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-tool-output-store-tests", Guid.NewGuid().ToString("n"));

    public ToolOutputBlobStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Size_policy_counts_utf8_bytes()
    {
        var exact = new string('a', ToolOutputBlobStore.MaximumInlineBytes - 4) + "😀";
        _ = await Assert.That(Encoding.UTF8.GetByteCount(exact))
            .IsEqualTo(ToolOutputBlobStore.MaximumInlineBytes);
        _ = await Assert.That(ToolOutputBlobStore.IsOversized(exact)).IsFalse();
        _ = await Assert.That(ToolOutputBlobStore.IsOversized(exact + "é")).IsTrue();
    }

    [Test]
    public async Task Persist_retries_collisions_and_returns_retrievable_notice(
        CancellationToken cancellationToken)
    {
        var collision = Path.Combine(_directory, "quiet-tree-arse.dat");
        await File.WriteAllTextAsync(collision, "existing", cancellationToken);
        var names = new Queue<string>(["quiet-tree-arse.dat", "bold-river-arse.dat"]);
        var store = new ToolOutputBlobStore(_directory, names.Dequeue);
        const string output = "large 😀 output";

        var notice = await store.Persist(output, cancellationToken);
        var path = Path.Combine(_directory, "bold-river-arse.dat");

        _ = await Assert.That(Path.IsPathFullyQualified(path)).IsTrue();
        _ = await Assert.That(await File.ReadAllTextAsync(collision, cancellationToken)).IsEqualTo("existing");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo(output);
        _ = await Assert.That(notice).IsEqualTo(
            $"Tool output exceeded 64 KiB and was saved to {path}.");
    }

    [Test]
    public async Task Persist_removes_a_file_when_writing_fails()
    {
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var store = new ToolOutputBlobStore(_directory, static () => "lost-cloud-arse.dat");

        _ = await Assert.That(async () => await store.Persist("output", canceled.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(File.Exists(Path.Combine(_directory, "lost-cloud-arse.dat"))).IsFalse();
    }
}
