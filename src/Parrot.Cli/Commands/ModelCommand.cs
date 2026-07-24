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

        await context.Output.WriteLineAsync($"  model is now {updated.Model}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
