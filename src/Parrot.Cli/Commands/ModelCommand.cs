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

        var updated = await context.Client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = context.UserSessionId, Model = arguments },
            cancellationToken: cancellationToken);

        // Persist it as the default for the next launch. This is the one place
        // a model choice is written to config -- the --model flag does not.
        context.Configuration.SetModel(updated.Model);

        await context.Output.WriteLineAsync($"  model is now {updated.Model} (saved)".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
