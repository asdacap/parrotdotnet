using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskDeclarationScrollbackValue(IReadOnlyList<PlanTaskDeclaration> declarations) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) =>
        AgentTaskDeclarationFormatter.FormatForWidth(declarations, context.Columns);
}
