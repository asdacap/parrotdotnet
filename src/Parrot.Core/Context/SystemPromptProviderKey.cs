namespace Parrot.Context;

internal static class SystemPromptProviderKey
{
    public static bool IsValid(string key)
    {
        var separator = key.IndexOf(':', StringComparison.Ordinal);
        return separator > 0
            && separator < key.Length - 1
            && !key.AsSpan().ContainsAny("\r\n\t ");
    }
}
