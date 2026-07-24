using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class ModeCommand : ISlashCommand
{
    public string Name => "/mode";

    public string Summary => "Switch the mode for this session";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (arguments.Length == 0)
        {
            await context.Output.WriteLineAsync("usage: /mode <id>".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        var updated = await context.Client.UpdateSessionAsync(
            new UpdateSessionRequest { UserSessionId = context.UserSessionId, Mode = arguments },
            cancellationToken: cancellationToken);

        context.Mode = updated.Mode;
        await context.Output.WriteLineAsync($"  mode is now {updated.Mode}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
