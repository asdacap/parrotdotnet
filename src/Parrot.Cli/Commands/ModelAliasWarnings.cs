using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal static class ModelAliasWarnings
{
    public static async Task<IReadOnlyList<string>> List(
        GeneratedParrot.ParrotClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            var listed = await client.ListModelAliasesAsync(
                new ListModelAliasesRequest(), cancellationToken: cancellationToken);
            return [.. listed.Aliases
                .Where(alias => alias.ModelString.Length == 0)
                .OrderBy(alias => alias.Name, StringComparer.Ordinal)
                .Select(alias => $"warning: model alias \"{alias.Name}\" is not configured")];
        }
        catch (RpcException)
        {
            return [];
        }
    }
}
