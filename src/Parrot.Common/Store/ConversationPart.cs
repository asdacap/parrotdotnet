namespace Parrot.Store;

internal sealed record ConversationPart
{
    private ConversationPart(
        ConversationPartKind kind,
        string text,
        string artifactId,
        string mediaType,
        string displayName)
    {
        Kind = kind;
        Text = text;
        ArtifactId = artifactId;
        MediaType = mediaType;
        DisplayName = displayName;
    }

    public ConversationPartKind Kind { get; }

    public string Text { get; }

    public string ArtifactId { get; }

    public string MediaType { get; }

    public string DisplayName { get; }

    public static ConversationPart TextPart(string text) =>
        new(ConversationPartKind.Text, text, string.Empty, string.Empty, string.Empty);

    public static ConversationPart ImageArtifact(string artifactId, string mediaType, string displayName) =>
        new(ConversationPartKind.ImageArtifact, string.Empty, artifactId, mediaType, displayName);

    public static ConversationPart ImageArtifact(ImageArtifactMetadata metadata) =>
        ImageArtifact(metadata.ArtifactId, metadata.MediaType, metadata.DisplayName);
}
