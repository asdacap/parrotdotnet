using Parrot.Process;

namespace Parrot.Core.Tests;

internal sealed class ProcessOutputBlobStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-output-store-tests", Guid.NewGuid().ToString("n"));

    public ProcessOutputBlobStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Persist_retries_collisions_and_removes_failed_writes(
        CancellationToken cancellationToken)
    {
        var collision = Path.Combine(_directory, "quiet-tree-arse.dat");
        await File.WriteAllTextAsync(collision, "existing", cancellationToken);
        var names = new Queue<string>(["quiet-tree-arse.dat", "bold-river-arse.dat"]);
        var store = new ProcessOutputBlobStore(_directory, names.Dequeue);
        var stdout = new ProcessOutput("out", string.Empty);
        var stderr = new ProcessOutput("err", string.Empty);

        var path = await store.Persist(7, stdout, stderr, cancellationToken);

        _ = await Assert.That(path).IsEqualTo(Path.Combine(_directory, "bold-river-arse.dat"));
        _ = await Assert.That(await File.ReadAllTextAsync(collision, cancellationToken)).IsEqualTo("existing");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken))
            .IsEqualTo("Process exited with code 7\n[stdout]\nout\n[stderr]\nerr");

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var canceledStore = new ProcessOutputBlobStore(
            _directory, static () => "lost-cloud-arse.dat");
        _ = await Assert.That(async () =>
                await canceledStore.Persist(0, stdout, stderr, canceled.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(File.Exists(Path.Combine(_directory, "lost-cloud-arse.dat"))).IsFalse();
    }
}
