using Parrot.Config;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class SelectionStatusProvider(PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:selection";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = new ScriptObject
        {
            ["profile"] = query.Profile,
            ["model"] = query.RequestedModel,
            ["parent_session_id"] = query.ParentSessionId,
            ["parent_session_name"] = query.ParentSessionName,
            ["has_parent"] = !string.IsNullOrWhiteSpace(query.ParentSessionId),
            ["has_parent_name"] = !string.IsNullOrWhiteSpace(query.ParentSessionName),
        };
        return ValueTask.FromResult(StatusObservation.AvailableText(
            templates.RenderStructured("status.selection", model, cancellationToken)));
    }
}
