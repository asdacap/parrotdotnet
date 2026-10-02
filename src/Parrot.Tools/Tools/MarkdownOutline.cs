using System.Globalization;

namespace Parrot.Tools;

internal sealed class MarkdownOutline
{
    private const int MaxHeadingLevel = 6;

    private readonly List<string> _headings = [];
    private bool _inFence;

    public bool IsEmpty => _headings.Count == 0;

    public void Record(int lineNumber, string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            _inFence = !_inFence;
            return;
        }

        var level = line.Length - line.TrimStart('#').Length;

        if (_inFence || level is < 1 or > MaxHeadingLevel || (line.Length > level && line[level] != ' '))
        {
            return;
        }

        _headings.Add(string.Concat(lineNumber.ToString(CultureInfo.InvariantCulture), ": ", line));
    }

    public string Render() => string.Join('\n', _headings);
}
