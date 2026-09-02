using System.Text;

namespace Parrot.Config;

internal sealed class PromptTemplate(string path, string text, IReadOnlySet<string> allowed, IReadOnlySet<string> required)
{
    public string Text { get; } = text;

    public IReadOnlySet<string> Allowed { get; } = allowed;

    public IReadOnlySet<string> Required { get; } = required;

    public IReadOnlyList<PromptTemplatePart> Parts { get; } = Parse(path, text, allowed);

    private static System.Collections.ObjectModel.ReadOnlyCollection<PromptTemplatePart> Parse(string path, string text, IReadOnlySet<string> allowed)
    {
        var parts = new List<PromptTemplatePart>();
        var literal = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '{' && index + 1 < text.Length && text[index + 1] == '{')
            {
                _ = literal.Append('{');
                index++;
                continue;
            }

            if (character == '}' && index + 1 < text.Length && text[index + 1] == '}')
            {
                _ = literal.Append('}');
                index++;
                continue;
            }

            if (character == '}')
            {
                throw new InvalidDataException($"{path}.template contains an unmatched closing brace");
            }

            if (character != '{')
            {
                _ = literal.Append(character);
                continue;
            }

            var close = text.IndexOf('}', index + 1);
            if (close < 0)
            {
                throw new InvalidDataException($"{path}.template contains an unmatched opening brace");
            }

            var placeholder = text[(index + 1)..close];
            if (!allowed.Contains(placeholder))
            {
                throw new InvalidDataException($"{path}.template contains unknown placeholder '{placeholder}'");
            }

            if (parts.Any(part => string.Equals(part.Placeholder, placeholder, StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"{path}.template contains duplicate placeholder '{placeholder}'");
            }

            if (literal.Length > 0)
            {
                parts.Add(new(literal.ToString(), null));
                _ = literal.Clear();
            }

            parts.Add(new(string.Empty, placeholder));
            index = close;
        }

        if (literal.Length > 0)
        {
            parts.Add(new(literal.ToString(), null));
        }

        return parts.AsReadOnly();
    }
}
