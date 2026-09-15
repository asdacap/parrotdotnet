using Parrot.Agent;

namespace Parrot.Statuses;

/// <summary>Describes the agent being observed; Scope is null when the session runs outside an agent scope.</summary>
internal sealed record StatusQuery(
    string SessionId,
    string ParentSessionId,
    string ParentSessionName,
    string Profile,
    string RequestedModel,
    IAgentSessionScope? Scope);
