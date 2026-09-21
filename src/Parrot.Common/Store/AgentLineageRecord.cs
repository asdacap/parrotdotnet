namespace Parrot.Store;

internal sealed record AgentLineageRecord(string SessionId, string ParentSessionId, string Name);
