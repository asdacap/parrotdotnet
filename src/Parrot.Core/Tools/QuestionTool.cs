using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Questions;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QuestionTool(QuestionBroker broker) : ITool
{
    public string Name => "question";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, QuestionJsonContext.Default.QuestionToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var wireQuestions = input.Questions ?? throw new FormatException("Tool arguments require an array 'questions'.");
            QuestionDefinition[] questions = [.. wireQuestions.Select(ToDomain)];
            var reply = await broker.Ask(questions, cancellationToken).ConfigureAwait(false);
            return reply.Kind == QuestionReplyKind.UserAway
                ? "The user is away."
                : FormatReply(questions, reply);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QuestionException or QuestionRejectedException)
        {
            return $"error: {failure.Message}";
        }
    }

    private static string FormatReply(IReadOnlyList<QuestionDefinition> questions, QuestionReply reply)
    {
        var answers = reply.Answers.ToDictionary(answer => answer.QuestionId, StringComparer.Ordinal);
        return string.Join("\n\n", questions.Select(question =>
        {
            var answer = answers[question.Id];
            var options = question.Options.ToDictionary(option => option.Id, StringComparer.Ordinal);
            var values = answer.OptionIds
                .Select(optionId => options[optionId].Label)
                .Concat(answer.Custom.Length == 0 ? [] : [answer.Custom]);
            return $"Question: {question.Prompt}\nAnswer: {string.Join(", ", values)}";
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

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("questions")]
        [ToolMinItems(1)]
        [ToolMaxItems(32)]
        [ToolRequired]
        public Question[]? Questions { get; init; }

        [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
        internal sealed partial class Question
        {
            [JsonPropertyName("id")]
            [ToolMinLength(1)]
            [ToolRequired]
            public string? Id { get; init; }

            [JsonPropertyName("header")]
            public string? Header { get; init; }

            [JsonPropertyName("prompt")]
            [ToolMinLength(1)]
            [ToolRequired]
            public string? Prompt { get; init; }

            [JsonPropertyName("options")]
            public Option[]? Options { get; init; }

            [JsonPropertyName("multiple")]
            public bool Multiple { get; init; }

            [JsonPropertyName("custom")]
            public bool Custom { get; init; }
        }

        [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
        internal sealed partial class Option
        {
            [JsonPropertyName("id")]
            [ToolMinLength(1)]
            [ToolRequired]
            public string? Id { get; init; }

            [JsonPropertyName("label")]
            [ToolMinLength(1)]
            [ToolRequired]
            public string? Label { get; init; }
        }
    }
}
