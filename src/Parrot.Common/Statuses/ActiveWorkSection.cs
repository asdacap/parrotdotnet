using System.Text;

namespace Parrot.Statuses;

/// <summary>Formats one active-work section with its heading and observations.</summary>
internal readonly record struct ActiveWorkSection(string Heading, IReadOnlyList<ActiveWorkObservation> Observations)
{
    public string Format()
    {
        if (Observations.Count == 0)
        {
            return string.Empty;
        }

        var section = new StringBuilder();
        _ = section.Append('\n').Append(Heading).Append(':');
        foreach (var item in Observations.OrderBy(static item => item.Id))
        {
            _ = section.Append("\n- ").Append(item.Id).Append(" (name: ").Append(item.Name).Append(')');
        }

        return section.ToString();
    }
}
