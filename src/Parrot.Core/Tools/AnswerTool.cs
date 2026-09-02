using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class AnswerTool(
    ChildQuestionCoordinator questions,
    AgentSession session) : ITool
{
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
            var reply = new QuestionReply([.. wireAnswers.Select(answer => new QuestionAnswer(
                answer.QuestionId ?? string.Empty,
                answer.OptionIds ?? [],
                answer.Custom ?? string.Empty))]);

            questions.Reply(session, agentSessionId, reply);
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.QuestionReplied(invocation, agentSessionId));
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
        public Answer[]? Answers { get; init; }

        internal sealed class Answer
        {
            [JsonPropertyName("question_id")]
            public string? QuestionId { get; init; }

            [JsonPropertyName("option_ids")]
            public string[]? OptionIds { get; init; }

            [JsonPropertyName("custom")]
            public string? Custom { get; init; }
        }
    }
}
