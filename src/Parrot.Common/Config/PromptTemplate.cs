namespace Parrot.Config;

internal sealed class PromptTemplate(
    IReadOnlySet<string> allowed,
    IReadOnlySet<string> required,
    IPromptTemplateEngine engine)
{
    public IReadOnlySet<string> Allowed { get; } = allowed;

    public IReadOnlySet<string> Required { get; } = required;

    public IPromptTemplateEngine Engine { get; } = engine;
}
