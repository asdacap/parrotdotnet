namespace Parrot;

internal static class Identifier
{
    public static string New() => Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture);
}
