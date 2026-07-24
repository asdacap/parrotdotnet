using Parrot.Auth;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;

namespace Parrot.Cli;

internal static class CommandDispatcher
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;
    public const int ExitFailure = 1;

    private const string ProviderId = "opencode-go";
    private const string DefaultModel = "deepseek-v4-pro";

    private const string UsageText = """
        parrot - a coding agent that is not too much

        Usage:
          parrot <command> [flags]

        Commands:
          help                        Print this message
          version                     Print the build version
          auth login --api-key-stdin  Store the opencode-go key read from stdin
          chat [--model <id>] <text>  Send one prompt and stream the reply

        M1 walking skeleton: one turn, no tools, no persistence.
        """;

    // The single top-level Run. Every other Run is a descendant of this call.
    public static async Task<int> Run(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var command = arguments.Count == 0 ? "help" : arguments[0];

        switch (command)
        {
            case "help" or "--help" or "-h":
                await output.WriteLineAsync(UsageText.AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitSuccess;

            case "version" or "--version":
                await output.WriteLineAsync($"{BuildInfo.ProductName} {BuildInfo.Version}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ExitSuccess;

            case "auth":
                return await Login(arguments, output, error, cancellationToken).ConfigureAwait(false);

            case "chat":
                return await Chat(arguments, output, error, cancellationToken).ConfigureAwait(false);

            default:
                await error.WriteLineAsync($"parrot: unknown command \"{command}\"".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await error.WriteLineAsync("Run \"parrot help\" for usage.".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ExitUsage;
        }
    }

    private static async Task<int> Login(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (arguments.Count < 2 || arguments[1] != "login")
        {
            await error.WriteLineAsync("usage: parrot auth login --api-key-stdin".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitUsage;
        }

        var key = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

        if (key.Length == 0)
        {
            await error.WriteLineAsync("parrot: no key on stdin".AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitUsage;
        }

        using var store = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        await store.Set(ProviderId, key, cancellationToken).ConfigureAwait(false);

        await output.WriteLineAsync($"stored a credential for {ProviderId}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        return ExitSuccess;
    }

    private static async Task<int> Chat(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var model = DefaultModel;
        var words = new List<string>();

        for (var index = 1; index < arguments.Count; index++)
        {
            if (arguments[index] == "--model" && index + 1 < arguments.Count)
            {
                model = arguments[++index];
            }
            else
            {
                words.Add(arguments[index]);
            }
        }

        if (words.Count == 0)
        {
            await error.WriteLineAsync("usage: parrot chat [--model <id>] <text>".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitUsage;
        }

        using var store = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        var key = await store.Get(ProviderId, cancellationToken).ConfigureAwait(false);

        if (key is null)
        {
            await error.WriteLineAsync(
                "parrot: no credential. Run: parrot auth login --api-key-stdin".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            return ExitFailure;
        }

        using var http = new HttpClient();
        var provider = new OpenAICompatibleProvider(
            ProviderId, new Uri("https://opencode.ai/zen/go/v1/"), key, http);

        // Local mode opens no socket: the generated client reaches the service
        // through the in-process invoker.
        var client = new Parrot.Protocol.Parrot.ParrotClient(new InProcessCallInvoker(new ParrotService(provider)));

        var prompt = string.Join(' ', words);

        return await BasicCli
            .Render(client, Identifier.New(), model, prompt, output, error, cancellationToken)
            .ConfigureAwait(false);
    }
}
