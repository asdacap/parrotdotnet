using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Enhanced;

internal static class EnhancedFinalMessageRenderer
{
    public static IReadOnlyList<string> Render(
        string source,
        string prefix,
        int columns,
        bool color,
        bool allowJson)
    {
        var markdown = allowJson && ToolYamlFormatter.TryFormat(source, out var yaml)
            ? $"```yaml\n{yaml}\n```"
            : source;
        return MarkdownRenderer.Render(prefix, markdown, columns, color);
    }
}
