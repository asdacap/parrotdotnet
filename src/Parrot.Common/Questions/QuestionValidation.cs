namespace Parrot.Questions;

internal static class QuestionValidation
{
    public static IReadOnlyList<QuestionDefinition> CopyQuestions(IReadOnlyList<QuestionDefinition> questions) =>
        [.. questions.Select(question => new QuestionDefinition(
            question.Header,
            question.Prompt,
            [.. question.Options],
            question.Multiple,
            question.Custom))];

    public static QuestionReply CopyReply(QuestionReply reply) =>
        new(reply.Kind, [.. reply.Answers.Select(answer => new QuestionAnswer(answer.Text))]);

    public static void ValidateQuestions(IReadOnlyList<QuestionDefinition> questions)
    {
        if (questions.Count is < 1 or > 32)
        {
            throw new QuestionException("question requests require between 1 and 32 questions");
        }

        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Prompt))
            {
                throw new QuestionException("question prompts cannot be empty");
            }

            var options = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in question.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Label))
                {
                    throw new QuestionException("question options cannot be empty");
                }

                if (!options.Add(option.Label))
                {
                    throw new QuestionException($"duplicate question option: {option.Label}");
                }
            }

            if (question.Options.Count == 0 && !question.Custom)
            {
                throw new QuestionException("question requires options or custom answers");
            }
        }
    }

    public static void ValidateReply(IReadOnlyList<QuestionDefinition> questions, QuestionReply reply)
    {
        if (reply.Kind != QuestionReplyKind.Answered)
        {
            throw new QuestionException("question replies require user answers");
        }

        if (reply.Answers.Count != questions.Count)
        {
            throw new QuestionException("question replies must contain one answer for every question in order");
        }

        if (reply.Answers.Any(answer => string.IsNullOrWhiteSpace(answer.Text)))
        {
            throw new QuestionException("question answers cannot be empty");
        }
    }
}
