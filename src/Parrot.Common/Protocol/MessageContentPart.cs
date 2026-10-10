using Parrot.Store;

namespace Parrot.Protocol;

public sealed partial class MessageContentPart
{
    internal static MessageContentPart FromPart(ConversationPart part) => part.Kind switch
    {
        ConversationPartKind.Text => new MessageContentPart { Text = part.Text },
        ConversationPartKind.ImageArtifact => new MessageContentPart { ArtifactId = part.ArtifactId },
        _ => throw new InvalidOperationException($"unsupported conversation part {part.Kind}"),
    };
}
