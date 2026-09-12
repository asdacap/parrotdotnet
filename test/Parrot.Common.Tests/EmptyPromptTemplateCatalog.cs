internal sealed class EmptyPromptTemplateCatalog : Parrot.Config.IPromptTemplateCatalog
{
    public string RenderSkills(string skills) => throw new NotSupportedException();

    public string RenderSelectedSkill(string name, string path, string content) => throw new NotSupportedException();

    public string RenderUnavailableSkill(string name, string message) => throw new NotSupportedException();

    public string Render(string id, IReadOnlyList<Parrot.Config.PromptTemplateArgument> arguments) => throw new NotSupportedException();

    public string RenderStructured(string id, Scriban.Runtime.ScriptObject arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
}
