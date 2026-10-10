using Parrot.Agent;

namespace Parrot.Questions;

/// <summary>Coordinates direct-child questions.</summary>
internal interface IChildQuestionCoordinator
{
    Task<QuestionReply> Ask(IAgentSession askingChild, IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken);

    /// <summary>Captures all currently pending direct-child questions.</summary>
    IReadOnlyList<PendingChildQuestionRequest> Pending();

    /// <summary>Captures pending questions only when the supplied agent owns this coordinator.</summary>
    IReadOnlyList<PendingChildQuestionRequest> PendingForParent(IAgentSession parent);

    /// <summary>Returns a reminder naming the children with pending questions, or null when none are pending.</summary>
    string? CompletionReminder();

    /// <summary>Answers the pending question of the direct child with that name.</summary>
    void Reply(string childName, QuestionReply reply);

    /// <summary>Authorizes the parent scope before answering its direct child.</summary>
    void ReplyFromParent(IAgentParentScope parentScope, string childName, QuestionReply reply);

    /// <summary>Fails pending child questions.</summary>
    void Dispose();
}
