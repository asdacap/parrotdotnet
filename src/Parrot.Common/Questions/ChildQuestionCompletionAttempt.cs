namespace Parrot.Questions;

internal sealed class ChildQuestionCompletionAttempt(Action? release, string? reminder) : IDisposable
{
    private Action? _release = release;

    public string? Reminder { get; } = reminder;

    public static ChildQuestionCompletionAttempt Block(string reminder) => new(null, reminder);

    public static ChildQuestionCompletionAttempt Reserve(Action release) => new(release, null);

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
