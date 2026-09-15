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

    public bool IsEnabledAfterInterruption => true;

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
            var agentName = input.AgentName
                ?? throw new FormatException("Tool arguments require a string 'agent_name'.");
            var wireAnswers = input.Answers
                ?? throw new FormatException("Tool arguments require an array 'answers'.");
            var reply = new QuestionReply([.. wireAnswers.Select(answer => new QuestionAnswer(answer ?? string.Empty))]);

            if (_parentScope is null)
            {
                questions.Reply(agentName, reply);
            }
            else
            {
                questions.ReplyFromParent(_parentScope, agentName, reply);
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
        [JsonPropertyName("agent_name")]
        public string? AgentName { get; init; }

        [JsonPropertyName("answers")]
        public string[]? Answers { get; init; }
    }
}
