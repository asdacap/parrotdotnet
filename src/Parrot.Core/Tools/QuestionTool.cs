using System.Text.Json;
using Parrot.Questions;

namespace Parrot.Tools;

internal sealed class QuestionTool(QuestionBroker broker) : ITool
{
    public string Name => "question";

    public string Description => "Ask the user structured questions when a decision or missing information is needed. Use this instead of asking questions in normal chat.";

    public string ParametersJson => QuestionToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, QuestionJsonContext.Default.QuestionToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var wireQuestions = input.Questions ?? throw new FormatException("Tool arguments require an array 'questions'.");
            QuestionDefinition[] questions = [.. wireQuestions.Select(ToDomain)];
            var reply = await broker.Ask(questions, cancellationToken).ConfigureAwait(false);
            return FormatReply(questions, reply);
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
