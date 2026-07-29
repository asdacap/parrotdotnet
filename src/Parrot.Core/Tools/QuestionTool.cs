using System.Text.Json;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionTool(QuestionBroker broker) : ITool
{
    public string Name => "question";

    public string Description => "Ask the user structured questions when a decision or missing information is needed. Use this instead of asking questions in normal chat.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"questions":{"type":"array","minItems":1,"maxItems":32,"items":{"type":"object","properties":{"id":{"type":"string","minLength":1},"header":{"type":"string"},"prompt":{"type":"string","minLength":1},"options":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","minLength":1},"label":{"type":"string","minLength":1}},"required":["id","label"],"additionalProperties":false}},"multiple":{"type":"boolean"},"custom":{"type":"boolean"}},"required":["id","prompt"],"additionalProperties":false}}},"required":["questions"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, QuestionJsonContext.Default.QuestionToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var questions = input.Questions ?? throw new FormatException("Tool arguments require an array 'questions'.");
            var reply = await broker.Ask([.. questions.Select(ToDomain)], cancellationToken).ConfigureAwait(false);
            var result = new QuestionToolResult
            {
                Answers = [.. reply.Answers.Select(answer => new QuestionWireAnswer
                {
                    QuestionId = answer.QuestionId,
                    OptionIds = [.. answer.OptionIds],
                    Custom = answer.Custom,
                })],
            };
            return JsonSerializer.Serialize(result, QuestionJsonContext.Default.QuestionToolResult);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QuestionException or QuestionRejectedException)
        {
            return $"error: {failure.Message}";
        }
    }

    private static QuestionDefinition ToDomain(QuestionWireDefinition question) => new(
        question.Id ?? string.Empty,
        question.Header ?? string.Empty,
        question.Prompt ?? string.Empty,
        [.. (question.Options ?? []).Select(option => new QuestionOption(
            option.Id ?? string.Empty,
            option.Label ?? string.Empty))],
        question.Multiple,
        question.Custom);
}
