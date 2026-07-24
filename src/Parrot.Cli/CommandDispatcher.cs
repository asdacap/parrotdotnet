using Grpc.Net.Client;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal static class CommandDispatcher
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;
    public const int ExitFailure = 1;

    private const string ProviderId = "opencode-go";
    private const string DefaultModel = "glm-5.2";

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

    // One handler for the process, which is what HttpClient wants anyway.
    private static readonly HttpClient Http = new();

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
        var provider = await ResolveProvider(error, cancellationToken).ConfigureAwait(false);

        if (provider is null)
        {
            return ExitFailure;
        }

        var paths = StatePaths.ResolveFromEnvironment();
        using var store = new SessionStore(
            paths.State, Directory.GetCurrentDirectory(), Environment.MachineName, BuiltinTools(), ProcessRunner.Locate());
        using var service = new ParrotService(provider, store);

        var listed = await ClientFor(service)
            .ListModelsAsync(new ListModelsRequest(), cancellationToken: cancellationToken);

        foreach (var model in listed.Models)
        {
            await output.WriteLineAsync($"{model.ProviderId}/{model.Id}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        return ExitSuccess;
    }

    // Resolves the credential and builds the provider. It deliberately does not
    // build the service: the caller owns that, and owning it is what makes the
    // disposal visible at the call site.
    private static async Task<ILLMProvider?> ResolveProvider(
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var store = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        var key = await store.Get(ProviderId, cancellationToken).ConfigureAwait(false);

        if (key is null)
        {
            await error.WriteLineAsync(
                "parrot: no credential. Run: parrot auth login --api-key-stdin".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var provider = new OpenAICompatibleProvider(
            ProviderId, new Uri("https://opencode.ai/zen/go/v1/"), key, Http);

        return provider;
    }

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

        var provider = await ResolveProvider(error, cancellationToken).ConfigureAwait(false);

        if (provider is null)
        {
            return ExitFailure;
        }

        var paths = StatePaths.ResolveFromEnvironment();
        using var store = new SessionStore(
            paths.State, Directory.GetCurrentDirectory(), Environment.MachineName, BuiltinTools(), ProcessRunner.Locate());
        using var service = new ParrotService(provider, store);

        await output.WriteLineAsync($"parrot serving on port {port} (ctrl-c to stop)".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return await GrpcServer.Run(service, port, cancellationToken).ConfigureAwait(false);
    }

    private static string RemoteAddress(string target) =>
        target.StartsWith("http", StringComparison.Ordinal) ? target : $"http://{target}";

    // The tools the agent may call. exec_command is the one that reaches the
    // sandbox; read_file is read-only.
    private static ToolRegistry BuiltinTools() =>
        new([new ExecCommandTool(), new ReadFileTool(), new AgentSpawnTool()]);

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
                remote, Renderer(basic), paths, configuration, model, prompt, output, error, cancellationToken)
                .ConfigureAwait(false);
        }

        var provider = await ResolveProvider(error, cancellationToken).ConfigureAwait(false);

        if (provider is null)
        {
            return ExitFailure;
        }

        using var store = new SessionStore(
            paths.State, Directory.GetCurrentDirectory(), Environment.MachineName, BuiltinTools(), ProcessRunner.Locate());
        using var service = new ParrotService(provider, store);

        return await Drive(
            ClientFor(service), Renderer(basic), paths, configuration, model, prompt, output, error, cancellationToken)
            .ConfigureAwait(false);
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
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var text = prompt;

        // Piped stdin is one answer, not a session. Scripts and CI depend on it.
        if (text.Length == 0 && Console.IsInputRedirected)
        {
            text = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (text.Length == 0)
            {
                return ExitUsage;
            }
        }

        using var credentials = new FileCredentialStore(paths.CredentialsFile);

        var session = await client.CreateSessionAsync(
            new CreateSessionRequest { Model = model }, cancellationToken: cancellationToken);

        var context = new SlashContext(
            client, credentials, configuration, ProviderId, session.Id, output, error);

        var driver = new CliDriver(client, renderer, BuildRegistry(model));

        return await driver.Run(context, text, Console.In, output, cancellationToken).ConfigureAwait(false);
    }
}
