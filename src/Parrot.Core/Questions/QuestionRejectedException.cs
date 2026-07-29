namespace Parrot.Questions;

public sealed class QuestionRejectedException : Exception
{
    public QuestionRejectedException()
    {
    }

    public QuestionRejectedException(string message)
        : base(message)
    {
    }

    public QuestionRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
