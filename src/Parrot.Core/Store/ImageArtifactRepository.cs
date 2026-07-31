namespace Parrot.Store;

internal sealed class ImageArtifactRepository(ImageArtifactStore store, EventRepository events)
{
    public Task<ImageArtifactMetadata> Persist(
        Stream source,
        string uploadId,
        string displayName,
        string origin,
        CancellationToken cancellationToken) =>
        Persist(source, uploadId, displayName, origin, string.Empty, -1, cancellationToken);

    public async Task<ImageArtifactMetadata> Persist(
        Stream source,
        string uploadId,
        string displayName,
        string origin,
        string declaredMediaType,
        long declaredByteLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        var metadata = await store.Persist(
            source,
            displayName,
            origin,
            declaredMediaType,
            declaredByteLength,
            cancellationToken).ConfigureAwait(false);
        return events.RecordImageArtifact(metadata, uploadId);
    }

    public Stream Open(string artifactId)
    {
        _ = events.ResolveImageArtifact(artifactId)
            ?? throw new FileNotFoundException("The image artifact does not exist.", artifactId);
        return store.Open(artifactId);
    }

    public ImageArtifactMetadata? Resolve(string artifactId) => events.ResolveImageArtifact(artifactId);

    public void Claim(string artifactId, string referenceId) => events.ClaimImageArtifact(artifactId, referenceId);

    public void Release(string artifactId, string referenceId) => events.ReleaseImageArtifact(artifactId, referenceId);

    public void RemoveStaleUnreferenced(DateTimeOffset before)
    {
        foreach (var artifact in events.RemoveStaleUnreferencedImageArtifacts(before))
        {
            store.Delete(artifact);
        }
    }
}
