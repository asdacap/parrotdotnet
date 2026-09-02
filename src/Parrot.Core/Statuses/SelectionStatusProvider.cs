using Parrot.Config;

namespace Parrot.Statuses;

internal sealed class SelectionStatusProvider(PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:selection";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = string.IsNullOrWhiteSpace(query.ParentSessionId)
            ? string.Empty
            : templates.Render("status.selection.parent", [
                new PromptTemplateArgument("parent", GetParent(query)),
            ]);
        return ValueTask.FromResult(StatusObservation.AvailableText(templates.Render("status.selection", [
            new PromptTemplateArgument("profile", query.Profile),
            new PromptTemplateArgument("model", query.RequestedModel),
            new PromptTemplateArgument("parent", parent),
        ])));
    }

    private static string GetParent(StatusQuery query) =>
        string.IsNullOrWhiteSpace(query.ParentSessionName)
            ? query.ParentSessionId
            : $"{query.ParentSessionId} ({query.ParentSessionName})";
}
