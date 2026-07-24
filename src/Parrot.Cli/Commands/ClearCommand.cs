using Parrot.Protocol;

namespace Parrot.Cli.Commands;

// A fresh session rather than a wiped one: the old session keeps its history
// and stays listed, which is what upstream's /clear does too.
internal sealed class ClearCommand(string defaultModel) : ISlashCommand
{
    public string Name => "/clear";

    public string Summary => "Start a fresh session, keeping the old one";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = arguments.Length > 0
            ? arguments
            : context.Model.Length > 0
                ? context.Model
                : defaultModel;

        var created = await context.Client.CreateSessionAsync(
            new CreateSessionRequest { Model = model }, cancellationToken: cancellationToken);

        context.UserSessionId = created.Id;
        context.Model = created.Model;

        await context.Output.WriteLineAsync($"  new session {created.Id}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}
