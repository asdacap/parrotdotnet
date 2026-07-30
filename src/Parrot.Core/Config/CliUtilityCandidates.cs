using System.Collections.ObjectModel;

namespace Parrot.Config;

internal sealed class CliUtilityCandidates(IEnumerable<string> expected, IEnumerable<string> optional)
{
    public ReadOnlyCollection<string> Expected { get; } = Array.AsReadOnly(expected.ToArray());

    public ReadOnlyCollection<string> Optional { get; } = Array.AsReadOnly(optional.ToArray());
}
