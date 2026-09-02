namespace Parrot.Questions;

internal static class QuestionValidation
{
    public static IReadOnlyList<QuestionDefinition> CopyQuestions(IReadOnlyList<QuestionDefinition> questions) =>
        [.. questions.Select(question => new QuestionDefinition(
            question.Id,
            question.Header,
            question.Prompt,
            [.. question.Options.Select(option => new QuestionOption(option.Id, option.Label))],
            question.Multiple,
            question.Custom))];

    public static QuestionReply CopyReply(QuestionReply reply) =>
        new(
            reply.Kind,
            [.. reply.Answers.Select(answer => new QuestionAnswer(answer.QuestionId, [.. answer.OptionIds], answer.Custom))]);

    public static void ValidateQuestions(IReadOnlyList<QuestionDefinition> questions)
    {
        if (questions.Count is < 1 or > 32)
        {
            throw new QuestionException("question requests require between 1 and 32 questions");
        }

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id))
            {
                throw new QuestionException("question ids cannot be empty");
            }

            if (!questionIds.Add(question.Id))
            {
                throw new QuestionException($"duplicate question id: {question.Id}");
            }

            if (string.IsNullOrWhiteSpace(question.Prompt))
            {
                throw new QuestionException($"question prompt cannot be empty: {question.Id}");
            }

            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in question.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.Label))
                {
                    throw new QuestionException($"question options require id and label: {question.Id}");
                }

                if (!optionIds.Add(option.Id))
                {
                    throw new QuestionException($"duplicate option id: {option.Id}");
                }
            }

            if (question.Options.Count == 0 && !question.Custom)
            {
                throw new QuestionException($"question requires options or custom answers: {question.Id}");
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
            throw new QuestionException("question replies must answer every question exactly once");
        }

        var definitions = questions.ToDictionary(question => question.Id, StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in reply.Answers)
        {
            if (!definitions.TryGetValue(answer.QuestionId, out var question))
            {
                throw new QuestionException($"unknown question id: {answer.QuestionId}");
            }

            if (!answered.Add(answer.QuestionId))
            {
                throw new QuestionException($"duplicate answer for question: {answer.QuestionId}");
            }

            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var optionId in answer.OptionIds)
            {
                if (!selected.Add(optionId))
                {
                    throw new QuestionException($"duplicate option answer: {optionId}");
                }

                if (!question.Options.Any(option => string.Equals(option.Id, optionId, StringComparison.Ordinal)))
                {
                    throw new QuestionException($"unknown option id: {optionId}");
                }
            }

            if (!question.Multiple && answer.OptionIds.Count > 1)
            {
                throw new QuestionException($"question does not allow multiple answers: {question.Id}");
            }

            if (answer.Custom.Length > 0 && !question.Custom)
            {
                throw new QuestionException($"question does not allow a custom answer: {question.Id}");
            }

            if (answer.OptionIds.Count == 0 && string.IsNullOrWhiteSpace(answer.Custom))
            {
                throw new QuestionException($"question answer cannot be empty: {question.Id}");
            }

            if (!question.Multiple && answer.OptionIds.Count > 0 && answer.Custom.Length > 0)
            {
                throw new QuestionException($"question does not allow multiple answers: {question.Id}");
            }
        }
    }
}
