using Parrot.Store;

namespace Parrot.Cli.Commands;

internal sealed class SessionsCommand(SessionIndex index, ISlashSession session, ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/sessions";

    public string Summary => "List sessions, reading meta.json only";

    public Task Run(CancellationToken cancellationToken) =>
        dialog.Show(
            [.. index.List()
                .OrderBy(meta => meta.CreatedAt, StringComparer.Ordinal)
                .Select(meta =>
                    $"{(string.Equals(meta.Id, session.Id, StringComparison.Ordinal) ? "*" : " ")} " +
                    $"{(meta.RootAgentName.Length == 0 ? "<unnamed>" : meta.RootAgentName)}  {meta.Id}  {meta.Model}")],
            cancellationToken);
}
