using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class EffortCommand : ISlashCommand
{
    public string Name => "/effort";

    public string Summary => "Switch the model effort for this session";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Model.Length == 0)
        {
            await context.Error.WriteLineAsync("  no model is selected".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        var listed = await context.Client.ListModelsAsync(
            new ListModelsRequest(), cancellationToken: cancellationToken);
        var current = ModelSelection.Resolve(listed.Models, context.Model);
        if (current is null)
        {
            await context.Error.WriteLineAsync(
                $"  unknown selected model: {context.Model}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        if (current.Model.Variants.Count == 0)
        {
            await context.Error.WriteLineAsync(
                $"  {current.BaseSelector} exposes no model efforts".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        var selected = arguments.Trim();
        if (selected.Length == 0)
        {
            foreach (var variant in current.Model.Variants)
            {
                await context.Output.WriteLineAsync($"  {variant.Name}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await context.Output.WriteAsync("effort> ".AsMemory(), cancellationToken).ConfigureAwait(false);
            selected = (await context.Input.ReadLine(cancellationToken).ConfigureAwait(false))?.Trim() ?? string.Empty;
            if (selected.Length == 0)
            {
                return SlashOutcome.Continue;
            }
        }

        var replacement = current.WithVariant(selected);
        if (replacement is null)
        {
            var message = $"  unknown model effort {selected}; choose one of: "
                + string.Join(", ", current.Model.Variants.Select(variant => variant.Name));
            await context.Error.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        var updated = await context.Client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = context.UserSessionId, Model = replacement.Selector },
            cancellationToken: cancellationToken);
        context.Model = updated.Model;
        context.Configuration.SetModel(updated.Model);

        await context.Output.WriteLineAsync(
            $"  Model effort selected: {replacement.Variant?.Name}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        return SlashOutcome.Continue;
    }
}
