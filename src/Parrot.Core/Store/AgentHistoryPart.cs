namespace Parrot.Store;

internal sealed record AgentHistoryPart(
    string Kind,
    string Text,
    string ArtifactId,
    string MediaType,
    string DisplayName);
