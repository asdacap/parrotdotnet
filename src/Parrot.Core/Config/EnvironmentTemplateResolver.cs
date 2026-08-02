using System.Text;

namespace Parrot.Config;

internal sealed class EnvironmentTemplateResolver(IReadOnlyDictionary<string, string> environment)
{
    private const int MaximumDepth = 32;
    private readonly Dictionary<string, string> _environment = (environment
        ?? throw new ArgumentNullException(nameof(environment)))
        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    public string Resolve(string template, string field)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        var index = 0;
        return ResolveSegment(template, field, ref index, depth: 0, nested: false, evaluate: true);
    }

    private static bool IsNameStart(char value) => value is '_' || char.IsAsciiLetter(value);

    private static bool IsNameCharacter(char value) => IsNameStart(value) || char.IsAsciiDigit(value);

    private static InvalidDataException Invalid(string field, string message) =>
        new($"{field} {message}");

    private string ResolveSegment(
        string template,
        string field,
        ref int index,
        int depth,
        bool nested,
        bool evaluate)
    {
        if (depth > MaximumDepth)
        {
            throw Invalid(field, "template nesting is too deep");
        }

        var result = new StringBuilder();
        while (index < template.Length)
        {
            if (template[index] == '}')
            {
                if (!nested)
                {
                    throw Invalid(field, "contains an unmatched '}'");
                }

                return result.ToString();
            }

            if (template[index] == '$' && index + 1 < template.Length && template[index + 1] == '{')
            {
                _ = result.Append(ResolveExpression(template, field, ref index, depth + 1, evaluate));
                continue;
            }

            _ = result.Append(template[index]);
            index++;
        }

        if (nested)
        {
            throw Invalid(field, "contains an unclosed environment template");
        }

        return result.ToString();
    }

    private string ResolveExpression(string template, string field, ref int index, int depth, bool evaluate)
    {
        index += 2;
        var nameStart = index;
        if (index >= template.Length || !IsNameStart(template[index]))
        {
            throw Invalid(field, "contains an invalid environment variable name");
        }

        index++;
        while (index < template.Length && IsNameCharacter(template[index]))
        {
            index++;
        }

        var name = template[nameStart..index];
        if (index >= template.Length)
        {
            throw Invalid(field, "contains an unclosed environment template");
        }

        if (template[index] == '}')
        {
            index++;
            return evaluate ? LookupRequired(name, field) : string.Empty;
        }

        if (template[index] != ':' || index + 1 >= template.Length || template[index + 1] != '-')
        {
            throw Invalid(field, "contains an unsupported environment template operator");
        }

        index += 2;
        string? value = null;
        var hasValue = evaluate && _environment.TryGetValue(name, out value) && value.Length > 0;
        var fallback = ResolveSegment(template, field, ref index, depth, nested: true, evaluate: evaluate && !hasValue);
        if (index >= template.Length || template[index] != '}')
        {
            throw Invalid(field, "contains an unclosed environment template");
        }

        index++;
        if (hasValue && value is not null)
        {
            return value;
        }

        return fallback;
    }

    private string LookupRequired(string name, string field) =>
        _environment.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw Invalid(field, $"requires nonempty environment variable {name}");
}
