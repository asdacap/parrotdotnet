using System.Runtime.InteropServices;

namespace Parrot.Context;

// Builds the system prompt from typed sources. Sampled once per context epoch,
// not per turn, so the baseline is immutable within an epoch (principle 4).
//
// M4 sources: a base prompt, the date, the platform, the working directory, and
// the global AGENTS.md file and the files found from the working directory upward.
// Skills and richer project metadata arrive with the milestones that own them.
internal sealed class SystemContextBuilder(
    string workingDirectory,
    string configDirectory,
    string date,
    string sessionContext)
{
    private const string BasePrompt =
        "You are parrot, a coding agent. You work in the user's project directory. "
        + "Prefer read, glob, and grep for inspection; apply_patch for edits; git_diff for review; "
        + "and exec_command only for shell commands. Filesystem access is determined by the active "
        + "security policy. Prefer small, verifiable steps.";

    public string Build()
    {
        var text = new System.Text.StringBuilder();
        _ = text.Append(BasePrompt).Append("\n\n");
        _ = text.Append("Date: ").Append(date).Append('\n');
        _ = text.Append("Platform: ").Append(RuntimeInformation.RuntimeIdentifier).Append('\n');
        _ = text.Append("Working directory: ").Append(workingDirectory).Append('\n');

        if (sessionContext.Length > 0)
        {
            _ = text.Append(sessionContext).Append('\n');
        }

        foreach (var (path, content) in AgentsFiles())
        {
            _ = text.Append("\n--- ").Append(path).Append(" ---\n").Append(content);
        }

        return text.ToString();
    }

    // From the working directory upward to the filesystem root, nearest last so
    // the most specific instructions win by appearing closest to the prompt.
    private List<(string Path, string Content)> AgentsFiles()
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
