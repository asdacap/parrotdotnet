using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ImageArtifactRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-image-repository-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Record_reopens_and_is_idempotent()
    {
        var resources = Resources("first");
        var image = Image();
        using (var database = SessionDatabase.Open(resources.DatabasePath))
        {
            var repository = new EventRepository(database);
            _ = repository.RecordImageArtifact(image, "upload-1");
            _ = repository.RecordImageArtifact(image, "upload-1");
        }

        using var reopened = SessionDatabase.Open(resources.DatabasePath);
        var restored = new EventRepository(reopened).ResolveImageArtifact(image.ArtifactId);
        _ = await Assert.That(restored).IsEqualTo(image);
    }

    [Test]
    public async Task Record_refuses_upload_id_with_different_artifact()
    {
        using var database = SessionDatabase.Open(Resources("conflict").DatabasePath);
        var repository = new EventRepository(database);
        _ = repository.RecordImageArtifact(Image(), "upload-1");

        _ = await Assert.That(() => repository.RecordImageArtifact(
            Image() with { ArtifactId = new string('b', 64), Sha256 = new string('b', 64) }, "upload-1"))
            .Throws<InputConflictException>();
    }

    [Test]
    public async Task Durable_conversation_reference_prevents_cleanup()
    {
        using var database = SessionDatabase.Open(Resources("durable-reference").DatabasePath);
        var repository = new EventRepository(database);
        var image = Image();
        _ = repository.RecordImageArtifact(image, "upload-1");
        repository.AppendConversation(
            new Parrot.Protocol.Event { Id = "event", AgentSessionId = "agent" },
            ConversationOrigin.UserInput,
            Parrot.Llm.LLMRole.User,
            [ConversationPart.ImageArtifact(image)],
            [],
            string.Empty);

        _ = await Assert.That(repository.RemoveStaleUnreferencedImageArtifacts(DateTimeOffset.UtcNow.AddMinutes(1))).IsEmpty();
    }

    [Test]
    public async Task Claim_prevents_cleanup_until_released()
    {
        using var database = SessionDatabase.Open(Resources("claim").DatabasePath);
        var repository = new EventRepository(database);
        var image = Image();
        _ = repository.RecordImageArtifact(image, "upload-1");
        repository.ClaimImageArtifact(image.ArtifactId, "message-1");

        _ = await Assert.That(repository.RemoveStaleUnreferencedImageArtifacts(DateTimeOffset.UtcNow.AddMinutes(1))).IsEmpty();
        repository.ReleaseImageArtifact(image.ArtifactId, "message-1");
        _ = await Assert.That(repository.RemoveStaleUnreferencedImageArtifacts(DateTimeOffset.UtcNow.AddMinutes(1)))
            .Contains(image);
    }

    private static ImageArtifactMetadata Image() => new(
        new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");

    private UserSessionResources Resources(string id) => new(
        new StatePaths(_root, _root, _root),
        UserSessionId.Parse(id),
        ProjectWorkspace.FromLaunchDirectory(Directory.CreateDirectory(Path.Combine(_root, "work")).FullName));
}
