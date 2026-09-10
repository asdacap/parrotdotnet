using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class AgentPathEnvironmentProvider(AgentPathEnvironment environment, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:agent-session-path-environment";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var displayed = new List<KeyValuePair<string, string>>();
        var entries = new List<string>();
        foreach (var entry in environment.Materialize().OrderByDescending(entry => entry.Key, StringComparer.Ordinal))
        {
            var path = entry.Value;
            foreach (var previous in displayed.OrderByDescending(previous => previous.Value.Length))
            {
                var relative = Path.GetRelativePath(previous.Value, entry.Value);
                if (relative == ".")
                {
                    path = $"${previous.Key}";
                    break;
                }

                if (!Path.IsPathRooted(relative) && relative != ".."
                    && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    path = $"${previous.Key}{Path.DirectorySeparatorChar}{relative}";
                    break;
                }
            }

            entries.Add(templates.Render("context.agent-path-environment-entry", [
                new PromptTemplateArgument("name", entry.Key),
                new PromptTemplateArgument("path", path),
            ]));
            displayed.Add(entry);
        }

        return new StaticSystemPrompt(templates.Render("context.agent-path-environment", [
            new PromptTemplateArgument("entries", string.Join('\n', entries)),
        ]));
    }
}
