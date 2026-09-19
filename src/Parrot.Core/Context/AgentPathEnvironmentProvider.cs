using Parrot.Agent;
using Parrot.Config;
using Scriban.Runtime;

namespace Parrot.Context;

internal sealed class AgentPathEnvironmentProvider(IAgentPathEnvironment environment, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:90-agent-path-environment";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var displayed = new List<KeyValuePair<string, string>>();
        var entries = new ScriptArray();
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

            entries.Add(new ScriptObject
            {
                ["name"] = entry.Key,
                ["path"] = path,
            });
            displayed.Add(entry);
        }

        return new StaticSystemPrompt(templates.RenderStructured(
            "context.agent-path-environment", new ScriptObject { ["entries"] = entries }, CancellationToken.None));
    }
}
