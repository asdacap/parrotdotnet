using System.Globalization;
using Parrot.Config;
using Parrot.Context;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class ContextStatusProvider(
    ContextSnapshot snapshot,
    IPromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:queues-context";

    public static IStatusProvider Create(ContextSnapshot snapshot, IPromptTemplateCatalog templates) =>
        new ContextStatusProvider(snapshot, templates);

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var model = new ScriptObject
        {
            ["available"] = snapshot.IsAvailable,
            ["estimated_tokens"] = TokenCountFormatter.Format(snapshot.EstimatedTokens),
            ["context_limit"] = TokenCountFormatter.Format(snapshot.ContextLimit),
            ["cadence"] = ContextCadence.NotificationInterval.ToString(CultureInfo.InvariantCulture),
            ["trigger"] = snapshot.TriggerPercent.ToString(CultureInfo.InvariantCulture),
        };
        if (snapshot.UsagePercent is { } usage)
        {
            model.Add("usage", usage.ToString(CultureInfo.InvariantCulture));
        }

        if (snapshot.HasContextLimitOverride)
        {
            model.Add("trigger_tokens", snapshot.TriggerTokens?.ToString(CultureInfo.InvariantCulture) ?? "unavailable");
        }

        return ValueTask.FromResult(StatusObservation.AvailableText(
            templates.RenderStructured(
                snapshot.HasContextLimitOverride ? "status.context-limit" : "status.context",
                model,
                cancellationToken)));
    }
}
