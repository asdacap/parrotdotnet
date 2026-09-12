namespace Parrot.Statuses;

internal readonly record struct StatusObservation(bool Available, string Text)
{
    public static StatusObservation Unavailable => new(false, string.Empty);

    public static StatusObservation AvailableText(string text) => new(true, text);
}
