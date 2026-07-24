using Grpc.Core;
using Grpc.Net.Client;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;

using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal static class CommandDispatcher
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;
    public const int ExitFailure = 1;

    private const string DefaultModel = "opencode-go/glm-5.2";

    private const string UsageText = """
        parrot - a coding agent that is not too much

        Usage:
          parrot                      Open an interactive session
          parrot <command> [flags]

        Commands:
          help                        Print this message
          version                     Print the build version
          auth login --api-key-stdin  Store the opencode-go key read from stdin
          models                      List the models the provider serves
          sessions                    List sessions, reading meta.json only
          chat [--model <id>] [text]  A session, or one prompt if text is given
          chat --connect host:port    Drive a session on a remote parrot serve
          serve [--port <n>]          Host the service for remote clients

        --basic forces the minimal renderer; the default is the enhanced one.

        Bare `parrot` is `parrot chat`. In a terminal that opens a REPL; with a
        prompt or piped stdin it answers once. /help lists the slash commands.
        """;

    // Used before the composition exists: the registry is assembled and OAuth
    // runs while reading credentials, which the graph's own client cannot serve.
    private static readonly HttpClient Http =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly IBrowserOpener Browser = new SystemBrowserOpener();

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

        // No arguments opens a session, matching upstream: the common case is
        // wanting to talk to it, not read help.
        var command = arguments.Count == 0 ? "chat" : arguments[0];

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

            case "models":
                return await Models(output, error, cancellationToken).ConfigureAwait(false);

            case "sessions":
                return await Sessions(output, cancellationToken).ConfigureAwait(false);

            case "chat":
                return await Chat(arguments, output, error, cancellationToken).ConfigureAwait(false);

            case "serve":
                return await Serve(arguments, output, error, cancellationToken).ConfigureAwait(false);

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
        if (arguments.Count < 3 || arguments[1] != "login")
        {
            await error.WriteLineAsync(
                "usage: parrot auth login <provider> [--api-key-stdin] [--device]".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitUsage;
        }

        var provider = arguments[2];
        using var store = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);

        if (arguments.Contains("--api-key-stdin"))
        {
            var key = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (key.Length == 0)
            {
                await error.WriteLineAsync("parrot: no key on stdin".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ExitUsage;
            }

            await AuthFlows.StoreApiKey(store, provider, key, cancellationToken).ConfigureAwait(false);
        }
        else if (provider == ChatGptProvider.ProviderId)
        {
            await AuthFlows
                .OAuthLogin(OAuthClient(), store, arguments.Contains("--device"), output, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await error.WriteLineAsync(
                $"parrot: {provider} needs a key: parrot auth login {provider} --api-key-stdin".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            return ExitUsage;
        }

        await output.WriteLineAsync($"stored a credential for {provider}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        return ExitSuccess;
    }

    private static async Task<int> Sessions(TextWriter output, CancellationToken cancellationToken)
    {
        var paths = StatePaths.ResolveFromEnvironment();
        var listed = new SessionIndex(paths.State).List();

        foreach (var meta in listed.OrderBy(session => session.CreatedAt, StringComparer.Ordinal))
        {
            await output.WriteLineAsync(
                $"{meta.Id}  {meta.Model}  {meta.WorkingDirectory}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (listed.Count == 0)
        {
            await output.WriteLineAsync("no sessions".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    private static async Task<int> Models(
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        using var composition = await Compose(credentials, error, cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        var listed = await ClientFor(composition.Service)
            .ListModelsAsync(new ListModelsRequest(), cancellationToken: cancellationToken);

        foreach (var model in listed.Models)
        {
            await output.WriteLineAsync($"{model.ProviderId}/{model.Id}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        if (listed.Models.Count == 0)
        {
            await output.WriteLineAsync("no providers are configured".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    // Assembles every provider a credential makes available, then builds the
    // composition around the selected one. Null when the selection cannot be
    // resolved. The credential store must outlive the composition, because the
    // OAuth providers refresh through it, so the caller owns both.
    private static async Task<Composition?> Compose(
        ICredentialStore credentials,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var registry = await new ProviderRegistryBuilder(
                Configuration.Load(StatePaths.ResolveFromEnvironment().ConfigFile), credentials, Http, Browser)
                .Build().ConfigureAwait(false);

            return new Composition(
                registry, Directory.GetCurrentDirectory(), Environment.MachineName);
        }
        catch (LLMProviderException failure)
        {
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    private static OpenAiOAuthClient OAuthClient() => new(Http, Browser, new OpenAiOAuthOptions());

    private static async Task<int> Serve(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var port = 8710;

        for (var index = 1; index < arguments.Count; index++)
        {
            if (arguments[index] == "--port" && index + 1 < arguments.Count
                && int.TryParse(
                    arguments[index + 1], System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                port = parsed;
                index++;
            }
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        using var composition = await Compose(credentials, error, cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        await output.WriteLineAsync($"parrot serving on port {port} (ctrl-c to stop)".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return await GrpcServer.Run(composition.Service, port, cancellationToken).ConfigureAwait(false);
    }

    private static string RemoteAddress(string target) =>
        target.StartsWith("http", StringComparison.Ordinal) ? target : $"http://{target}";

    // Composed by hand, per AGENTS.md: no container, and the registry is the
    // one place that knows which commands exist.
    private static SlashCommandRegistry BuildRegistry(string defaultModel)
    {
        var commands = new List<ISlashCommand>
        {
            new ExitCommand(),
            new VersionCommand(),
            new ModelCommand(),
            new ModelsCommand(),
            new SessionsCommand(),
            new ClearCommand(defaultModel),
            new AuthCommand(ReadSecret),
        };

        var registry = new SlashCommandRegistry(commands);
        commands.Add(new HelpCommand(registry));

        return registry;
    }

    // Not ReadLine: a key must not land in the terminal scrollback, nor in a
    // screen recording.
    private static string ReadSecret()
    {
        var typed = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    _ = typed.Remove(typed.Length - 1, 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                _ = typed.Append(key.KeyChar);
            }
        }
    }

    // Local mode opens no socket: the generated client reaches the service
    // through the in-process invoker.
    private static GeneratedParrot.ParrotClient ClientFor(ParrotService service) =>
        new(new InProcessCallInvoker(service));

    private static async Task<int> Chat(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var paths = StatePaths.ResolveFromEnvironment();
        var configuration = Configuration.Load(paths.ConfigFile);

        // The saved model is the default; the built-in one is only the fallback
        // for a fresh install with no config yet.
        var model = configuration.Model.Length > 0 ? configuration.Model : DefaultModel;
        var connect = string.Empty;
        var basic = false;
        var words = new List<string>();

        for (var index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                // A per-invocation override; unlike /model it does not persist.
                case "--model" when index + 1 < arguments.Count:
                    model = arguments[++index];
                    break;

                case "--connect" when index + 1 < arguments.Count:
                    connect = arguments[++index];
                    break;

                case "--basic":
                    basic = true;
                    break;

                default:
                    words.Add(arguments[index]);
                    break;
            }
        }

        var prompt = string.Join(' ', words);

        // Remote: the server owns the provider, the state, and the tools; this
        // process is only a client of the same contract (principle 11).
        if (connect.Length > 0)
        {
            using var channel = GrpcChannel.ForAddress(RemoteAddress(connect));
            var remote = new GeneratedParrot.ParrotClient(channel);

            return await Drive(
                remote, Renderer(basic), paths, configuration, model, prompt, Console.In, output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        using var composition = await Compose(credentials, error, cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        return await Drive(
            ClientFor(composition.Service),
            Renderer(basic),
            paths,
            configuration,
            model,
            prompt,
            Console.In,
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    // EnhancedCli by default in a terminal; BasicCli when asked, or when output
    // is redirected and ANSI would only add noise. They share no rendering.
    private static ITurnRenderer Renderer(bool basic) =>
        basic || Console.IsOutputRedirected ? new BasicCli() : new EnhancedCli();

    // Builds the session and hands it to the driver. Local and remote take the
    // same path from here: the driver does not know which client it holds.
    private static async Task<int> Drive(
        GeneratedParrot.ParrotClient client,
        ITurnRenderer renderer,
        StatePaths paths,
        Configuration configuration,
        string model,
        string prompt,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var text = prompt;

        // Piped stdin is one answer, not a session. Scripts and CI depend on it.
        if (text.Length == 0 && Console.IsInputRedirected)
        {
            text = (await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (text.Length == 0)
            {
                return ExitUsage;
            }
        }

        using var credentials = new FileCredentialStore(paths.CredentialsFile);

        UserSession session;

        try
        {
            session = await client.CreateSessionAsync(
                new CreateSessionRequest { Model = model }, cancellationToken: cancellationToken);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            // A selection the registry cannot resolve is a usage error, not a crash.
            await error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitFailure;
        }

        var context = new SlashContext(
            client,
            credentials,
            OAuthClient(),
            configuration,
            ProviderRegistryBuilder.BuildableProviderIds(configuration),
            session.Id,
            input,
            output,
            error);

        var driver = new CliDriver(client, renderer, BuildRegistry(model));

        return await driver.Run(context, text, input, output, cancellationToken).ConfigureAwait(false);
    }
}
