namespace Parrot.Process;

internal readonly record struct ProcessSignal
{
    public ProcessSignal(int value)
    {
        if (value is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Process signal must be between 1 and 64.");
        }

        Value = value;
    }

    public int Value { get; }
}
