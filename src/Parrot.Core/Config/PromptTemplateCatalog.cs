using System.Collections.ObjectModel;
using System.Text;

namespace Parrot.Config;

internal sealed class PromptTemplateCatalog
{
    private readonly ReadOnlyDictionary<string, PromptTemplate> _templates;

    public PromptTemplateCatalog(IReadOnlyDictionary<string, PromptTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = new ReadOnlyDictionary<string, PromptTemplate>(
            new Dictionary<string, PromptTemplate>(templates, StringComparer.Ordinal));
    }

    public string Render(string id, IReadOnlyList<PromptTemplateArgument> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_templates.TryGetValue(id, out var template))
        {
            throw new InvalidDataException($"prompt_templates.{id} is not defined");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (!template.Allowed.Contains(argument.Name))
            {
                throw new InvalidDataException($"prompt_templates.{id} does not allow argument '{argument.Name}'");
            }

            if (!values.TryAdd(argument.Name, argument.Value))
            {
                throw new InvalidDataException($"prompt_templates.{id} received duplicate argument '{argument.Name}'");
            }
        }

        foreach (var required in template.Required)
        {
            if (!values.ContainsKey(required))
            {
                throw new InvalidDataException($"prompt_templates.{id} requires argument '{required}'");
            }
        }

        var result = new StringBuilder(template.Text.Length);
        foreach (var part in template.Parts)
        {
            _ = result.Append(part.Placeholder is null ? part.Text : values[part.Placeholder]);
        }

        return result.ToString();
    }
}
