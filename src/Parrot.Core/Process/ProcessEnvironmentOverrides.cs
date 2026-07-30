using System.Collections.ObjectModel;

namespace Parrot.Process;

internal sealed class ProcessEnvironmentOverrides(IEnumerable<KeyValuePair<string, string>> entries)
{
    private readonly ReadOnlyCollection<KeyValuePair<string, string>> _entries = Array.AsReadOnly(
        entries
            .Select(Validate)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray());

    public static ProcessEnvironmentOverrides Empty { get; } = new([]);

    public IReadOnlyList<KeyValuePair<string, string>> Entries => _entries;

    private static KeyValuePair<string, string> Validate(KeyValuePair<string, string> entry)
    {
        if (entry.Key.Length == 0
            || entry.Key.Contains('=', StringComparison.Ordinal)
            || entry.Key.Contains('\0', StringComparison.Ordinal)
            || entry.Value.Contains('\0', StringComparison.Ordinal))
        {
            throw new FormatException("Tool argument 'env' contains an invalid environment value.");
        }

        return entry;
    }
}
