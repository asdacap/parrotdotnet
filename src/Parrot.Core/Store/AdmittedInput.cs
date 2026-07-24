using Parrot.Protocol;

namespace Parrot.Store;

// A prompt that has been accepted but is not yet part of the conversation.
//
// It is a record rather than a row shape because the drain reads it back and
// promotes it: the id names it in the events, the message id is the sender's
// and is what makes admission idempotent, and the delivery decides which
// boundary promotes it.
internal sealed record AdmittedInput(string Id, string MessageId, string Content, Delivery Delivery);
