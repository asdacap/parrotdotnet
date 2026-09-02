using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class AgentsPrompt(string workingDirectory, string configDirectory, PromptTemplateCatalog templates) : ISystemPrompt
{
    private string _epochContext = string.Empty;
    private bool _renewed;

    public void RenewEpoch()
    {
        var sections = new List<string>();

        foreach (var (path, content) in FindFiles())
        {
            sections.Add(templates.Render("context.agents-file", [
                new PromptTemplateArgument("path", path),
                new PromptTemplateArgument("content", content),
            ]));
        }

        _epochContext = string.Join("\n\n", sections);
        _renewed = true;
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return _renewed
            ? _epochContext
            : throw new InvalidOperationException("The system context has not been sampled for this epoch.");
    }

    private List<(string Path, string Content)> FindFiles()
    {
        var found = new List<(string Path, string Content)>();
        var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));

        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "AGENTS.md");

            if (File.Exists(path))
            {
                found.Add((path, File.ReadAllText(path)));
            }

            directory = directory.Parent;
        }

        found.Reverse();
        var globalPath = Path.Combine(configDirectory, "AGENTS.md");

        if (File.Exists(globalPath) && !found.Exists(file => string.Equals(file.Path, globalPath, StringComparison.Ordinal)))
        {
            found.Insert(0, (globalPath, File.ReadAllText(globalPath)));
        }

        return found;
    }
}
