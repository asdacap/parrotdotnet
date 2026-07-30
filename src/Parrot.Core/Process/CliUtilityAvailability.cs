using System.Collections.ObjectModel;
using Parrot.Config;

namespace Parrot.Process;

internal sealed class CliUtilityAvailability
{
    private CliUtilityAvailability(
        IEnumerable<string> availableExpected,
        IEnumerable<string> missingExpected,
        IEnumerable<string> availableOptional)
    {
        AvailableExpected = CopySorted(availableExpected);
        MissingExpected = CopySorted(missingExpected);
        AvailableOptional = CopySorted(availableOptional);
    }

    public ReadOnlyCollection<string> AvailableExpected { get; }

    public ReadOnlyCollection<string> MissingExpected { get; }

    public ReadOnlyCollection<string> AvailableOptional { get; }

    public static CliUtilityAvailability Inspect(
        CliUtilityCandidates candidates,
        ExecutableLocator locator)
    {
        var availableExpected = new List<string>();
        var missingExpected = new List<string>();
        var availableOptional = new List<string>();

        foreach (var command in candidates.Expected)
        {
            (locator.Locate(command).Length > 0 ? availableExpected : missingExpected).Add(command);
        }

        foreach (var command in candidates.Optional)
        {
            if (locator.Locate(command).Length > 0)
            {
                availableOptional.Add(command);
            }
        }

        return new CliUtilityAvailability(availableExpected, missingExpected, availableOptional);
    }

    private static ReadOnlyCollection<string> CopySorted(IEnumerable<string> commands) =>
        Array.AsReadOnly(commands.Order(StringComparer.Ordinal).ToArray());
}
