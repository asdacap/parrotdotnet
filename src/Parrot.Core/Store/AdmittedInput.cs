using Parrot.Protocol;

namespace Parrot.Store;

// A prompt that has been accepted but is not yet part of the conversation.
//
// It is a record rather than a row shape because the drain reads it back and
// promotes it: the id names it in the events, the message id is the sender's
// and is what makes admission idempotent, and the delivery decides which
// boundary promotes it.
internal sealed record AdmittedInput(
    string Id,
    string MessageId,
    IReadOnlyList<ConversationPart> Parts,
    Delivery Delivery)
{
    public AdmittedInput(string id, string messageId, string content, Delivery delivery)
        : this(id, messageId, [ConversationPart.TextPart(content)], delivery)
    {
    }

    public string Content => string.Concat(
        Parts.Where(part => part.Kind == ConversationPartKind.Text).Select(part => part.Text));
}
