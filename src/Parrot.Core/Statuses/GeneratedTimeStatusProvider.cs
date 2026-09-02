using System.Globalization;
using Parrot.Config;

namespace Parrot.Statuses;

internal sealed class GeneratedTimeStatusProvider(
    TimeProvider timeProvider,
    PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:generated-time";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(StatusObservation.AvailableText(templates.Render("status.generated-time", [
            new PromptTemplateArgument(
                "time",
                timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
        ])));
    }
}
