using Parrot.Questions;

namespace Parrot.Agent;

/// <summary>Provides the owning agent's parent topology and authority over direct children.</summary>
internal interface IAgentParentScope
{
    bool HasParent { get; }

    IAgentSessionScope? Parent { get; }

    AgentCompletionDeliveryPolicy DeliveryPolicy { get; }

    IChildQuestionCoordinator ChildQuestions { get; }

    AgentPolicyLineage PolicyLineage { get; }

    string OwnerSessionId { get; }

    /// <summary>Validates that an identity matches this parent topology.</summary>
    void Validate(AgentIdentity identity);

    /// <summary>Resolves a direct child only while the owner still accepts work.</summary>
    IAgentSessionScope AuthorizeDirectChild(string childSessionId);

    /// <summary>Returns the registered owner scope or rejects an unbound or shutting-down scope.</summary>
    IAgentSessionScope RequireOwnerScope();

    /// <summary>Checks that the scope and its child registry belong to this owner.</summary>
    void ValidateOwnerScope(IAgentSessionScope scope);
}
