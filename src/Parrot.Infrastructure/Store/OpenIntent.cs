namespace Parrot.Store;

internal sealed record OpenIntent(
    OpenOperation Operation,
    UserSessionId SessionId,
    string WorkingDirectory,
    string CanonicalWorkspaceIdentity);
