namespace Parrot.Store;

// A message id reused for a different prompt. Admission is idempotent on the
// sender's message id, which only works while one id means one prompt: two
// prompts under one id is the sender contradicting itself, and neither
// answering the first nor the second would be right.
//
// A component-specific type, per MIGRATION.md section 4.
public sealed class InputConflictException : Exception
{
    public InputConflictException(string message)
        : base(message)
    {
    }

    public InputConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InputConflictException()
    {
    }
}
