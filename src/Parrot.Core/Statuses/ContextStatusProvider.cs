using System.Globalization;
using Parrot.Config;
using Parrot.Context;

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

        var template = snapshot.IsAvailable ? "status.context" : "status.context-unavailable";
        var arguments = new List<PromptTemplateArgument>
        {
            new("estimated_tokens", snapshot.EstimatedTokens.ToString(CultureInfo.InvariantCulture)),
            new("context_limit", snapshot.ContextLimit.ToString(CultureInfo.InvariantCulture)),
            new("cadence", ContextCadence.NotificationInterval.ToString(CultureInfo.InvariantCulture)),
            new("trigger", snapshot.TriggerPercent.ToString(CultureInfo.InvariantCulture)),
        };
        if (snapshot.UsagePercent is { } usage)
        {
            arguments.Add(new PromptTemplateArgument("usage", usage.ToString(CultureInfo.InvariantCulture)));
        }

        return ValueTask.FromResult(StatusObservation.AvailableText(templates.Render(template, arguments)));
    }
}
