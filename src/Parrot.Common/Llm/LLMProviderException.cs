namespace Parrot.Llm;

public sealed class LLMProviderException : Exception
{
    public LLMProviderException(string message)
        : base(message)
    {
    }

    public LLMProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LLMProviderException()
    {
    }
}
