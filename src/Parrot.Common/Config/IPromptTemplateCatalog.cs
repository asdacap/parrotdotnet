using Scriban.Runtime;

namespace Parrot.Config;

/// <summary>Renders configured prompt templates.</summary>
internal interface IPromptTemplateCatalog
{
    /// <summary>Renders the skills context prompt.</summary>
    string RenderSkills(string skills);

    /// <summary>Renders the selected-skill context prompt.</summary>
    string RenderSelectedSkill(string name, string path, string content);

    /// <summary>Renders the unavailable-skill context prompt.</summary>
    string RenderUnavailableSkill(string name, string message);

    /// <summary>Renders a prompt using scalar arguments.</summary>
    string Render(string id, IReadOnlyList<PromptTemplateArgument> arguments);

    /// <summary>Renders a prompt using structured script arguments.</summary>
    string RenderStructured(string id, ScriptObject arguments, CancellationToken cancellationToken);
}
