using System.Collections.ObjectModel;
using Scriban.Runtime;

namespace Parrot.Config;

internal sealed class PromptTemplateCatalog : IPromptTemplateCatalog
{
    private readonly ReadOnlyDictionary<string, PromptTemplate> _templates;

    public PromptTemplateCatalog(IReadOnlyDictionary<string, PromptTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = new ReadOnlyDictionary<string, PromptTemplate>(
            new Dictionary<string, PromptTemplate>(templates, StringComparer.Ordinal));
    }

    public string RenderSkills(string skills) =>
        Render("context.skills", [new PromptTemplateArgument("skills", skills)]);

    public string RenderSelectedSkill(string name, string path, string content) =>
        Render(
            "context.skill-selected",
            [
                new PromptTemplateArgument("name", name),
                new PromptTemplateArgument("path", path),
                new PromptTemplateArgument("content", content),
            ]);

    public string RenderUnavailableSkill(string name, string message) =>
        Render(
            "context.skill-unavailable",
            [
                new PromptTemplateArgument("name", name),
                new PromptTemplateArgument("message", message),
            ]);

    public string Render(string id, IReadOnlyList<PromptTemplateArgument> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_templates.TryGetValue(id, out var template))
        {
            throw new InvalidDataException($"prompt_templates.{id} is not defined");
        }

        var values = new ScriptObject();
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

        return RenderStructured(id, values, CancellationToken.None);
    }

    public string RenderStructured(string id, ScriptObject arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_templates.TryGetValue(id, out var template))
        {
            throw new InvalidDataException($"prompt_templates.{id} is not defined");
        }

        foreach (var argument in arguments)
        {
            if (!template.Allowed.Contains(argument.Key))
            {
                throw new InvalidDataException($"prompt_templates.{id} does not allow argument '{argument.Key}'");
            }
        }

        foreach (var required in template.Required)
        {
            if (!arguments.ContainsKey(required))
            {
                throw new InvalidDataException($"prompt_templates.{id} requires argument '{required}'");
            }
        }

        return template.Engine.Render(arguments, cancellationToken);
    }
}
