using Parrot.Protocol;

namespace Parrot.Store;

// An input that has just become part of the conversation. The drain needs
// both halves: the input to append to the history it is about to send, and the
// event to publish once the promotion has committed.
internal sealed record Promotion(AdmittedInput Input, Event Published);
