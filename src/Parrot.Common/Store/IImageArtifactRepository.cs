namespace Parrot.Store;

/// <summary>Persists, resolves, and reference-counts validated image artifacts.</summary>
internal interface IImageArtifactRepository
{
    /// <summary>Persists an image and records its upload reference.</summary>
    Task<ImageArtifactMetadata> Persist(
        Stream source,
        string uploadId,
        string displayName,
        string origin,
        CancellationToken cancellationToken);

    /// <summary>Persists an image with declared media type and byte length validation.</summary>
    Task<ImageArtifactMetadata> PersistDeclared(
        Stream source,
        string uploadId,
        string displayName,
        string origin,
        string declaredMediaType,
        long declaredByteLength,
        CancellationToken cancellationToken);

    /// <summary>Opens a previously recorded image artifact for reading.</summary>
    Stream Open(string artifactId);

    /// <summary>Resolves metadata for a recorded image artifact.</summary>
    ImageArtifactMetadata? Resolve(string artifactId);

    /// <summary>Claims an artifact for a durable reference.</summary>
    void Claim(string artifactId, string referenceId);

    /// <summary>Releases an artifact durable reference.</summary>
    void Release(string artifactId, string referenceId);

    /// <summary>Removes artifacts older than the supplied instant that have no references.</summary>
    void RemoveStaleUnreferenced(DateTimeOffset before);
}
