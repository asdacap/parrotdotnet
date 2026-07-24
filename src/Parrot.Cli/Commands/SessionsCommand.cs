using Parrot.State;
using Parrot.Store;

namespace Parrot.Cli.Commands;

internal sealed class SessionsCommand : ISlashCommand
{
    public string Name => "/sessions";

    public string Summary => "List sessions, reading meta.json only";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = new SessionIndex(StatePaths.ResolveFromEnvironment().State).List();

        foreach (var meta in listed.OrderBy(session => session.CreatedAt, StringComparer.Ordinal))
        {
            var marker = string.Equals(meta.Id, context.UserSessionId, StringComparison.Ordinal) ? "*" : " ";

            await context.Output
                .WriteLineAsync($" {marker} {meta.Id}  {meta.Model}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        return SlashOutcome.Continue;
    }
}
