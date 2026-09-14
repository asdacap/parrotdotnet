using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionTool(IQuestionRequester requester) : ITool
{
    public string Name => "question";

    public bool IsEnabledAfterInterruption => true;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, QuestionJsonContext.Default.QuestionToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var wireQuestions = input.Questions ?? throw new FormatException("Tool arguments require an array 'questions'.");
            QuestionDefinition[] questions = [.. wireQuestions.Select(question => new QuestionDefinition(
                question.Header ?? string.Empty,
                question.Prompt ?? string.Empty,
                [.. (question.Options ?? []).Select(option => new Parrot.Questions.QuestionOption(option.Label, option.Description))],
                question.Multiple,
                question.Custom))];
            var reply = await requester.Ask(questions, cancellationToken).ConfigureAwait(false);
            return reply.Kind == QuestionReplyKind.UserAway
                ? ToolResultFormatter.Text(invocation, "The user is away.")
                : FormatReply(invocation, questions, reply);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or AgentRegistryException or QuestionException or QuestionRejectedException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    private static string FormatReply(ToolInvocation invocation, IReadOnlyList<QuestionDefinition> questions, QuestionReply reply) =>
        string.Join("\n\n", questions.Zip(reply.Answers, (question, answer) =>
            ToolResultFormatter.QuestionAnswer(invocation, question.Prompt, answer.Text)));

    internal sealed class Input
    {
        [JsonPropertyName("questions")]
        public Question[]? Questions { get; init; }

        internal sealed class Question
        {
            [JsonPropertyName("header")]
            public string? Header { get; init; }

            [JsonPropertyName("prompt")]
            public string? Prompt { get; init; }

            [JsonPropertyName("options")]
            public QuestionOption[]? Options { get; init; }

            [JsonPropertyName("multiple")]
            public bool Multiple { get; init; }

            [JsonPropertyName("custom")]
            public bool Custom { get; init; }
        }
    }
}
