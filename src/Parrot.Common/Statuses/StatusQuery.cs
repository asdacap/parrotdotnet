using Parrot.Agent;

namespace Parrot.Statuses;

internal sealed record StatusQuery(
    string SessionId,
    string ParentSessionId,
    string ParentSessionName,
    string Profile,
    string RequestedModel)
{
    internal static StatusQuery Create(IAgentSession session, string profile, string requestedModel) =>
        new(session.SessionId, session.ParentSessionId, session.ParentSessionName, profile, requestedModel);
}
