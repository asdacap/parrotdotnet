namespace Parrot.Process;

internal readonly record struct LinuxSignal
{
    public LinuxSignal(int value)
    {
        if (value is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Linux signal must be between 1 and 64.");
        }

        Value = value;
    }

    public int Value { get; }
}
