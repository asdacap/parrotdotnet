using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionTool(IQuestionRequester requester) : ITool
{
    public string Name => "question";

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, QuestionJsonContext.Default.QuestionToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var wireQuestions = input.Questions ?? throw new FormatException("Tool arguments require an array 'questions'.");
            QuestionDefinition[] questions = [.. wireQuestions.Select(ToDomain)];
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

    private static string FormatReply(ToolInvocation invocation, IReadOnlyList<QuestionDefinition> questions, QuestionReply reply)
    {
        var answers = reply.Answers.ToDictionary(answer => answer.QuestionId, StringComparer.Ordinal);
        return string.Join("\n\n", questions.Select(question =>
        {
            var answer = answers[question.Id];
            var options = question.Options.ToDictionary(option => option.Id, StringComparer.Ordinal);
            var values = answer.OptionIds
                .Select(optionId => options[optionId].Label)
                .Concat(answer.Custom.Length == 0 ? [] : [answer.Custom]);
            return ToolResultFormatter.QuestionAnswer(invocation, question.Prompt, string.Join(", ", values));
        }));
    }

    private static QuestionDefinition ToDomain(Input.Question question) => new(
        question.Id ?? string.Empty,
        question.Header ?? string.Empty,
        question.Prompt ?? string.Empty,
        [.. (question.Options ?? []).Select(option => new QuestionOption(
            option.Id ?? string.Empty,
            option.Label ?? string.Empty))],
        question.Multiple,
        question.Custom);

    internal sealed class Input
    {
        [JsonPropertyName("questions")]
        public Question[]? Questions { get; init; }

        internal sealed class Question
        {
            [JsonPropertyName("id")]
            public string? Id { get; init; }

            [JsonPropertyName("header")]
            public string? Header { get; init; }

            [JsonPropertyName("prompt")]
            public string? Prompt { get; init; }

            [JsonPropertyName("options")]
            public Option[]? Options { get; init; }

            [JsonPropertyName("multiple")]
            public bool Multiple { get; init; }

            [JsonPropertyName("custom")]
            public bool Custom { get; init; }
        }

        internal sealed class Option
        {
            [JsonPropertyName("id")]
            public string? Id { get; init; }

            [JsonPropertyName("label")]
            public string? Label { get; init; }
        }
    }
}
