using Parrot.Protocol;

namespace Parrot.Cli.Commands;

// The model is session state, so switching it is an UpdateSession rather than
// something the next prompt carries.
internal sealed class ModelCommand : ISlashCommand
{
    public string Name => "/model";

    public string Summary => "Switch the model for this session";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (arguments.Length == 0)
        {
            await context.Output.WriteLineAsync("usage: /model <id>".AsMemory(), cancellationToken)
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

        var selected = ModelSelection.Resolve(listed.Models, arguments.Trim());
        if (selected is null)
        {
            await context.Error.WriteLineAsync(
                $"  unknown model: {arguments.Trim()}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        if (selected.Variant is null)
        {
            selected = current.Variant is { } currentVariant
                ? selected.WithVariant(currentVariant.Name) ?? selected.WithFirstVariant()
                : selected.WithFirstVariant();
        }

        var updated = await context.Client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = context.UserSessionId, Model = selected.Selector },
            cancellationToken: cancellationToken);

        // Persist it as the default for the next launch. This is the one place
        // a model choice is written to config -- the --model flag does not.
        context.Model = updated.Model;
        context.Configuration.SetModel(updated.Model);

        await context.Output.WriteLineAsync($"  model is now {updated.Model} (saved)".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
