using System.Globalization;
using Parrot.Config;
using Parrot.Context;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class ContextStatusProvider(
    ContextSnapshot snapshot,
    PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:queues-context";

    public static IStatusProvider Create(ContextSnapshot snapshot, PromptTemplateCatalog templates) =>
        new ContextStatusProvider(snapshot, templates);

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var model = new ScriptObject
        {
            ["available"] = snapshot.IsAvailable,
            ["estimated_tokens"] = snapshot.EstimatedTokens.ToString(CultureInfo.InvariantCulture),
            ["context_limit"] = snapshot.ContextLimit.ToString(CultureInfo.InvariantCulture),
            ["cadence"] = ContextCadence.NotificationInterval.ToString(CultureInfo.InvariantCulture),
            ["trigger"] = snapshot.TriggerPercent.ToString(CultureInfo.InvariantCulture),
        };
        if (snapshot.UsagePercent is { } usage)
        {
            model.Add("usage", usage.ToString(CultureInfo.InvariantCulture));
        }

        return ValueTask.FromResult(StatusObservation.AvailableText(
            templates.RenderStructured("status.context", model, cancellationToken)));
    }
}
