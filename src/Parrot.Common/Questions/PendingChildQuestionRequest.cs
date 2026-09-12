namespace Parrot.Questions;

internal sealed record PendingChildQuestionRequest(
    string Id,
    string AskingAgentSessionId,
    string AskingAgentName,
    string ParentAgentSessionId,
    IReadOnlyList<QuestionDefinition> Questions);
