using Parrot.Config;

namespace Parrot.Agent;

internal sealed record AgentIdentity(
    string SessionId,
    string ParentSessionId,
    string ParentSessionName,
    string Name,
    int Depth,
    AgentScope Scope,
    AgentPolicyLineage PolicyLineage,
    PromptTemplateCatalog PromptTemplates)
{
    public string Context
    {
        get
        {
            var scope = Scope.Format(Depth);
            if (Depth == 0)
            {
                return scope;
            }

            var identity = PromptTemplates.Render(
                "system.agent-identity",
                [
                    new("session_id", SessionId),
                    new("parent_session_id", ParentSessionId),
                    new("parent_session_name", ParentSessionName),
                    new("agent_name", Name),
                    new("depth", Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]);
            return scope.Length == 0
                ? identity
                : $"{identity}\n\n{scope}";
        }
    }

    public static AgentIdentity Main(
        string sessionId,
        string rootAgentName,
        PromptTemplateCatalog promptTemplates) =>
        new(
            sessionId,
            string.Empty,
            string.Empty,
            rootAgentName,
            0,
            AgentScope.Empty(promptTemplates),
            AgentPolicyLineage.Root(),
            promptTemplates);

    public static AgentIdentity Child(
        string sessionId,
        string parentSessionId,
        string parentSessionName,
        string name,
        int depth,
        AgentScope scope,
        PromptTemplateCatalog promptTemplates) =>
        new(
            sessionId,
            parentSessionId,
            parentSessionName,
            name,
            depth,
            scope,
            AgentPolicyLineage.Root(),
            promptTemplates);

    public static AgentIdentity ChildWithPolicyLineage(
        string sessionId,
        string parentSessionId,
        string parentSessionName,
        string name,
        int depth,
        AgentScope scope,
        AgentPolicyLineage policyLineage,
        PromptTemplateCatalog promptTemplates) =>
        new(sessionId, parentSessionId, parentSessionName, name, depth, scope, policyLineage, promptTemplates);
}
