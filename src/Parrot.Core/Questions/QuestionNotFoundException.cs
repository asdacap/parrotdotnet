namespace Parrot.Questions;

public sealed class QuestionNotFoundException : Exception
{
    public QuestionNotFoundException()
    {
    }

    public QuestionNotFoundException(string message)
        : base(message)
    {
    }

    public QuestionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
