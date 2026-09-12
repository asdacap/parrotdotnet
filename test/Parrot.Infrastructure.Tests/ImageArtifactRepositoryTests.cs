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
        var image = new ImageArtifactMetadata(
            new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");
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
    [Arguments("pixel.png", "test", "upload-1")]
    [Arguments("renamed.png", "test", "upload-1")]
    [Arguments("pixel.png", "read_image", "upload-1")]
    [Arguments("renamed.png", "read_image", "upload-1")]
    [Arguments("pixel.png", "test", "upload-2")]
    [Arguments("renamed.png", "test", "upload-2")]
    [Arguments("pixel.png", "read_image", "upload-2")]
    [Arguments("renamed.png", "read_image", "upload-2")]
    public async Task Record_reuses_content_and_preserves_first_labels(string displayName, string origin, string uploadId)
    {
        var resources = Resources("labels");
        var image = new ImageArtifactMetadata(
            new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");
        var repeatedImage = image with { DisplayName = displayName, Origin = origin };
        using (var database = SessionDatabase.Open(resources.DatabasePath))
        {
            var repository = new EventRepository(database);
            _ = repository.RecordImageArtifact(image, "upload-1");
            _ = await Assert.That(repository.RecordImageArtifact(repeatedImage, uploadId)).IsEqualTo(image);
        }

        using var reopened = SessionDatabase.Open(resources.DatabasePath);
        var restoredRepository = new EventRepository(reopened);
        _ = await Assert.That(restoredRepository.ResolveImageArtifact(image.ArtifactId)).IsEqualTo(image);
        _ = await Assert.That(restoredRepository.RecordImageArtifact(repeatedImage, uploadId)).IsEqualTo(image);
        _ = await Assert.That(() => restoredRepository.RecordImageArtifact(
            repeatedImage with { ArtifactId = new string('b', 64), Sha256 = new string('b', 64) }, uploadId))
            .Throws<InputConflictException>();
        _ = await Assert.That(restoredRepository.RecordImageArtifact(image, "upload-1")).IsEqualTo(image);
    }

    [Test]
    [Arguments("Sha256")]
    [Arguments("MediaType")]
    [Arguments("ByteLength")]
    [Arguments("Width")]
    [Arguments("Height")]
    [Arguments("FrameCount")]
    [Arguments("AggregatePixels")]
    public async Task Record_refuses_inconsistent_content_without_mutation(string changedField)
    {
        using var database = SessionDatabase.Open(Resources("metadata-conflict").DatabasePath);
        var repository = new EventRepository(database);
        var image = new ImageArtifactMetadata(
            new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");
        var conflictingImage = changedField switch
        {
            "Sha256" => image with { Sha256 = new string('b', 64) },
            "MediaType" => image with { MediaType = "image/jpeg" },
            "ByteLength" => image with { ByteLength = 2 },
            "Width" => image with { Width = 2 },
            "Height" => image with { Height = 2 },
            "FrameCount" => image with { FrameCount = 2 },
            "AggregatePixels" => image with { AggregatePixels = 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
        _ = repository.RecordImageArtifact(image, "upload-1");

        foreach (var uploadId in new[] { "upload-1", "upload-2" })
        {
            _ = await Assert.That(() => repository.RecordImageArtifact(conflictingImage, uploadId))
                .Throws<InputConflictException>();
            _ = await Assert.That(repository.ResolveImageArtifact(image.ArtifactId)).IsEqualTo(image);
            _ = await Assert.That(repository.RecordImageArtifact(image, "upload-1")).IsEqualTo(image);
        }

        var differentImage = image with { ArtifactId = new string('b', 64), Sha256 = new string('b', 64) };
        _ = await Assert.That(repository.RecordImageArtifact(differentImage, "upload-2")).IsEqualTo(differentImage);
    }

    [Test]
    public async Task Record_refuses_upload_id_with_different_artifact()
    {
        using var database = SessionDatabase.Open(Resources("conflict").DatabasePath);
        var repository = new EventRepository(database);
        var image = new ImageArtifactMetadata(
            new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");
        _ = repository.RecordImageArtifact(image, "upload-1");
        var differentImage = image with { ArtifactId = new string('b', 64), Sha256 = new string('b', 64) };

        _ = await Assert.That(() => repository.RecordImageArtifact(differentImage, "upload-1"))
            .Throws<InputConflictException>();
        _ = await Assert.That(repository.ResolveImageArtifact(image.ArtifactId)).IsEqualTo(image);
        _ = await Assert.That(repository.ResolveImageArtifact(differentImage.ArtifactId)).IsNull();
        _ = await Assert.That(repository.RecordImageArtifact(image, "upload-1")).IsEqualTo(image);
    }

    [Test]
    public async Task Durable_conversation_reference_prevents_cleanup()
    {
        using var database = SessionDatabase.Open(Resources("durable-reference").DatabasePath);
        var repository = new EventRepository(database);
        var image = new ImageArtifactMetadata(
            new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");
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
        var image = new ImageArtifactMetadata(
            new string('a', 64), new string('a', 64), "image/png", 1, 1, 1, 1, 1, "pixel.png", "test");
        _ = repository.RecordImageArtifact(image, "upload-1");
        repository.ClaimImageArtifact(image.ArtifactId, "message-1");

        _ = await Assert.That(repository.RemoveStaleUnreferencedImageArtifacts(DateTimeOffset.UtcNow.AddMinutes(1))).IsEmpty();
        repository.ReleaseImageArtifact(image.ArtifactId, "message-1");
        _ = await Assert.That(repository.RemoveStaleUnreferencedImageArtifacts(DateTimeOffset.UtcNow.AddMinutes(1)))
            .Contains(image);
    }

    private UserSessionResources Resources(string id) => new(
        new StatePaths(_root, _root, _root),
        UserSessionId.Parse(id),
        ProjectWorkspace.FromLaunchDirectory(Directory.CreateDirectory(Path.Combine(_root, "work")).FullName));
}
