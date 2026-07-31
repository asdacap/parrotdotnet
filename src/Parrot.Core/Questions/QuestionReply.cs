namespace Parrot.Questions;

internal sealed record QuestionReply(QuestionReplyKind Kind, IReadOnlyList<QuestionAnswer> Answers)
{
    public QuestionReply(IReadOnlyList<QuestionAnswer> answers)
        : this(QuestionReplyKind.Answered, answers)
    {
    }

    public static QuestionReply UserAway { get; } = new(QuestionReplyKind.UserAway, []);
}
