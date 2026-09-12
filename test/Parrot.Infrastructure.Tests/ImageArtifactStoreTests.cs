using System.Security.Cryptography;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ImageArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-image-artifact-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Persist_validates_and_stores_original_png_bytes(CancellationToken cancellationToken)
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        var resources = Resources();
        var store = new ImageArtifactStore(resources);

        await using var input = new MemoryStream(bytes);
        var artifact = await store.Persist(input, "pixel.png", "test", cancellationToken);

        _ = await Assert.That(artifact.ArtifactId).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        _ = await Assert.That(artifact.Sha256).IsEqualTo(artifact.ArtifactId);
        _ = await Assert.That(artifact.MediaType).IsEqualTo("image/png");
        _ = await Assert.That(artifact.Width).IsEqualTo(1);
        _ = await Assert.That(artifact.Height).IsEqualTo(1);
        _ = await Assert.That(artifact.FrameCount).IsEqualTo(1);
        _ = await Assert.That(Directory.EnumerateFiles(resources.ArtifactDirectory, ".staging-*")).IsEmpty();

        await using var stored = store.Open(artifact.ArtifactId);
        using var result = new MemoryStream();
        await stored.CopyToAsync(result, cancellationToken);
        _ = await Assert.That(Convert.ToHexString(result.ToArray())).IsEqualTo(Convert.ToHexString(bytes));
    }

    [Test]
    public async Task Persist_rejects_non_images_and_removes_staging(CancellationToken cancellationToken)
    {
        var resources = Resources();
        var store = new ImageArtifactStore(resources);
        await using var input = new MemoryStream([1, 2, 3]);

        _ = await Assert.That(async () => await store.Persist(input, "bad.bin", "test", cancellationToken))
            .Throws<InvalidDataException>();
        _ = await Assert.That(Directory.EnumerateFiles(resources.ArtifactDirectory, ".staging-*")).IsEmpty();
    }

    private UserSessionResources Resources() => new(
        new StatePaths(_root, _root, _root),
        UserSessionId.Parse("images"),
        ProjectWorkspace.FromLaunchDirectory(Directory.CreateDirectory(Path.Combine(_root, "work")).FullName));
}
