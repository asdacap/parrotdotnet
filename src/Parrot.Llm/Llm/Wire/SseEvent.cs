namespace Parrot.Llm.Wire;

// One decoded Server-Sent Events record.
internal readonly record struct SseEvent(string Event, string Data, string Id);
