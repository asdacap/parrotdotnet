using Scriban.Runtime;

namespace Parrot.Config;

/// <summary>Renders a compiled prompt using explicitly supplied script values.</summary>
internal interface IPromptTemplateEngine
{
    string Render(ScriptObject arguments, CancellationToken cancellationToken);
}
