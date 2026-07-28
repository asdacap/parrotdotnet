using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal static class ModelAliasSelection
{
    public static async Task<string> Resolve(
        GeneratedParrot.ParrotClient client,
        string selector,
        CancellationToken cancellationToken)
    {
        if (selector.Contains('/', StringComparison.Ordinal))
        {
            return selector;
        }

        try
        {
            var listed = await client.ListModelAliasesAsync(
                new ListModelAliasesRequest(), cancellationToken: cancellationToken);
            return listed.Aliases.FirstOrDefault(alias =>
                string.Equals(alias.Name, selector, StringComparison.Ordinal))?.ModelString ?? selector;
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unimplemented)
        {
            return selector;
        }
    }
}
