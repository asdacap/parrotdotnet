using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerTool(IChildQuestionCoordinator questions) : ITool
{
    private readonly IAgentParentScope? _parentScope;

    public AnswerTool(IChildQuestionCoordinator questions, IAgentParentScope parentScope)
        : this(questions) => _parentScope = parentScope;

    public string Name => "answer";

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                QuestionJsonContext.Default.AnswerToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var agentSessionId = input.AgentSessionId
                ?? throw new FormatException("Tool arguments require a string 'agent_session_id'.");
            var wireAnswers = input.Answers
                ?? throw new FormatException("Tool arguments require an array 'answers'.");
            var reply = new QuestionReply([.. wireAnswers.Select(answer => new QuestionAnswer(answer ?? string.Empty))]);
            var agentName = questions.ResolveDirectChildName(agentSessionId);

            if (_parentScope is null)
            {
                questions.Reply(agentSessionId, reply);
            }
            else
            {
                questions.ReplyFromParent(_parentScope, agentSessionId, reply);
            }

            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.QuestionReplied(invocation, agentName));
        }
        catch (Exception failure) when (failure is JsonException
            or FormatException
            or AgentRegistryException
            or QuestionException
            or QuestionRejectedException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("agent_session_id")]
        public string? AgentSessionId { get; init; }

        [JsonPropertyName("answers")]
        public string[]? Answers { get; init; }
    }
}
