namespace Parrot.Questions;

public sealed class QuestionException : Exception
{
    public QuestionException()
    {
    }

    public QuestionException(string message)
        : base(message)
    {
    }

    public QuestionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
