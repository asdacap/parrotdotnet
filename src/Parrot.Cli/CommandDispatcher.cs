using Grpc.Net.Client;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using YamlDotNet.Core;

using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class CommandDispatcher(
    Interrupts interrupts,
    TextWriter output,
    TextWriter error,
    HttpClient httpClient,
    IBrowserOpener browserOpener,
    OpenAiOAuthClient oauthClient)
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;
    public const int ExitFailure = 1;

    private const string DefaultModel = "opencode-go/glm-5.2";
    private const string DefaultMode = "build";

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
          chat [--model <id>] [--variant <name>] [--mode <id>] [text]
                                      A session, or one prompt if text is given
          chat --connect host:port    Drive a session on a remote parrot serve
          serve [--port <n>]          Host the service for remote clients

        --basic forces the minimal renderer; the default is the enhanced one.
        --variant is a deprecated, nonpersistent reasoning-variant override.

        Bare `parrot` is `parrot chat`. In a terminal that opens a REPL; with a
        prompt or piped stdin it answers once. /help lists the slash commands.
        """;

    // The single top-level Run. Every other Run is a descendant of this call.
    public async Task<int> Run(
        IReadOnlyList<string> arguments,
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
                return await Authenticate(arguments, cancellationToken).ConfigureAwait(false);

            case "models":
                return await ListModels(cancellationToken).ConfigureAwait(false);

            case "sessions":
                return await ListSessions(cancellationToken).ConfigureAwait(false);

            case "chat":
                return await RunChat(arguments, cancellationToken).ConfigureAwait(false);

            case "serve":
                return await RunServer(arguments, cancellationToken).ConfigureAwait(false);

            default:
                await error.WriteLineAsync($"parrot: unknown command \"{command}\"".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await error.WriteLineAsync("Run \"parrot help\" for usage.".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ExitUsage;
        }
    }

    internal static async Task<string?> OverrideVariant(
        GeneratedParrot.ParrotClient client,
        string selector,
        string variant,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var canonicalSelector = await ModelAliasSelection.Resolve(client, selector, cancellationToken)
            .ConfigureAwait(false);
        var listed = await client.ListModelsAsync(new ListModelsRequest(), cancellationToken: cancellationToken);
        var current = ModelSelection.Resolve(listed.Models, canonicalSelector);
        if (current is null)
        {
            await error.WriteLineAsync(
                $"parrot: unknown model selection {selector}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return null;
        }

        var replacement = current.WithVariant(variant);
        if (replacement is null)
        {
            var message = $"parrot: model {current.BaseSelector} does not support effort {variant}; choose one of: "
                + string.Join(", ", current.Model.Variants.Select(item => item.Name));
            await error.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return null;
        }

        return replacement.Selector;
    }

    private static string NormalizeRemoteAddress(string target) =>
        target.StartsWith("http", StringComparison.Ordinal) ? target : $"http://{target}";

    private async Task<int> Authenticate(
        IReadOnlyList<string> arguments,
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
            try
            {
                await AuthFlows
                    .OAuthLogin(
                        oauthClient,
                        store,
                        arguments.Contains("--device"),
                        output,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AuthException failure)
            {
                await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ExitFailure;
            }
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

    private async Task<int> ListSessions(CancellationToken cancellationToken)
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

    private async Task<int> ListModels(CancellationToken cancellationToken)
    {
        var configuration = await LoadConfiguration(cancellationToken).ConfigureAwait(false);

        if (configuration is null)
        {
            return ExitFailure;
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        await using var composition = await BuildComposition(credentials, configuration, cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        var listed = await new GeneratedParrot.ParrotClient(new InProcessCallInvoker(composition.Service))
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
    private async Task<Composition?> BuildComposition(
        ICredentialStore credentials,
        Configuration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var registry = await new ProviderRegistryBuilder(configuration, credentials, httpClient, browserOpener)
                .Build(cancellationToken).ConfigureAwait(false);

            return new Composition(
                registry, configuration, Directory.GetCurrentDirectory(), Environment.MachineName);
        }
        catch (LLMProviderException failure)
        {
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    private async Task<int> RunServer(
        IReadOnlyList<string> arguments,
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

        var configuration = await LoadConfiguration(cancellationToken).ConfigureAwait(false);

        if (configuration is null)
        {
            return ExitFailure;
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        await using var composition = await BuildComposition(credentials, configuration, cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        await output.WriteLineAsync($"parrot serving on port {port} (ctrl-c to stop)".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return await GrpcServer.Run(composition.Service, port, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Configuration?> LoadConfiguration(CancellationToken cancellationToken)
    {
        try
        {
            var paths = StatePaths.ResolveFromEnvironment();
            return Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
        }
        catch (Exception failure) when (failure is InvalidDataException or YamlException)
        {
            await error.WriteLineAsync($"parrot: invalid configuration: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    private async Task<int> RunChat(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var paths = StatePaths.ResolveFromEnvironment();
        var model = DefaultModel;
        var modelOverridden = false;
        var mode = DefaultMode;
        var connect = string.Empty;
        var variant = (string?)null;
        var basic = false;
        var words = new List<string>();

        for (var index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                // A per-invocation override; unlike /model it does not persist.
                case "--model" when index + 1 < arguments.Count:
                    model = arguments[++index];
                    modelOverridden = true;
                    break;

                case "--variant" when index + 1 < arguments.Count
                    && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal):
                    variant = arguments[++index];
                    break;

                case "--variant":
                    await error.WriteLineAsync(
                        "usage: parrot chat --variant <name>".AsMemory(), cancellationToken).ConfigureAwait(false);
                    return ExitUsage;

                case "--mode" when index + 1 < arguments.Count:
                    mode = arguments[++index];
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

        var configuration = await LoadConfiguration(cancellationToken).ConfigureAwait(false);

        if (configuration is null)
        {
            return ExitFailure;
        }

        // The saved model is the default; the built-in one is only the fallback
        // for a fresh install with no config yet.
        model = !modelOverridden && configuration.Model.Length > 0 ? configuration.Model : model;
        var prompt = string.Join(' ', words);

        // Remote: the server owns the provider, the state, and the tools; this
        // process is only a client of the same contract (principle 11).
        if (connect.Length > 0)
        {
            using var channel = GrpcChannel.ForAddress(
                NormalizeRemoteAddress(connect),
                new GrpcChannelOptions
                {
                    MaxReceiveMessageSize = GrpcTransportLimits.MessageBytes,
                    MaxSendMessageSize = GrpcTransportLimits.MessageBytes,
                });
            var remote = new GeneratedParrot.ParrotClient(channel);
            if (variant is not null)
            {
                var overridden = await OverrideVariant(remote, model, variant, error, cancellationToken)
                    .ConfigureAwait(false);
                if (overridden is null)
                {
                    return ExitUsage;
                }

                model = overridden;
            }

            using var remoteCredentials = new FileCredentialStore(paths.CredentialsFile);
            var remoteProviderIds = ProviderRegistryBuilder.BuildableProviderIds(configuration);

            if (basic || Console.IsOutputRedirected)
            {
                var cli = new BasicCli(
                    remote,
                    interrupts,
                    remoteCredentials,
                    oauthClient,
                    configuration,
                    remoteProviderIds,
                    model,
                    mode,
                    prompt,
                    Console.IsInputRedirected,
                    Console.In,
                    output,
                    error);
                return await cli.Run(cancellationToken).ConfigureAwait(false);
            }

            using var remoteRawTerminal = UnixRawTerminal.Open();
            if (remoteRawTerminal is null)
            {
                var cli = new BasicCli(
                    remote,
                    interrupts,
                    remoteCredentials,
                    oauthClient,
                    configuration,
                    remoteProviderIds,
                    model,
                    mode,
                    prompt,
                    Console.IsInputRedirected,
                    Console.In,
                    output,
                    error);
                return await cli.Run(cancellationToken).ConfigureAwait(false);
            }

            var remoteTerminal = new ConsoleTerminal(output, error, remoteRawTerminal);
            var remoteChat = new EnhancedComposition(
                remote,
                interrupts,
                remoteCredentials,
                oauthClient,
                configuration,
                remoteProviderIds,
                new EnhancedChatRequest(new CreateSessionRequest { Model = model, Mode = mode }, prompt),
                remoteTerminal);
            return await remoteChat.Cli.Run(cancellationToken).ConfigureAwait(false);
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        await using var composition = await BuildComposition(credentials, configuration, cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(composition.Service));
        if (variant is not null)
        {
            var overridden = await OverrideVariant(client, model, variant, error, cancellationToken)
                .ConfigureAwait(false);
            if (overridden is null)
            {
                return ExitUsage;
            }

            model = overridden;
        }

        var providerIds = ProviderRegistryBuilder.BuildableProviderIds(configuration);

        if (basic || Console.IsOutputRedirected)
        {
            var cli = new BasicCli(
                client,
                interrupts,
                credentials,
                oauthClient,
                configuration,
                providerIds,
                model,
                mode,
                prompt,
                Console.IsInputRedirected,
                Console.In,
                output,
                error);
            return await cli.Run(cancellationToken).ConfigureAwait(false);
        }

        using var rawTerminal = UnixRawTerminal.Open();
        if (rawTerminal is null)
        {
            var cli = new BasicCli(
                client,
                interrupts,
                credentials,
                oauthClient,
                configuration,
                providerIds,
                model,
                mode,
                prompt,
                Console.IsInputRedirected,
                Console.In,
                output,
                error);
            return await cli.Run(cancellationToken).ConfigureAwait(false);
        }

        var terminal = new ConsoleTerminal(output, error, rawTerminal);
        var enhancedChat = new EnhancedComposition(
            client,
            interrupts,
            credentials,
            oauthClient,
            configuration,
            providerIds,
            new EnhancedChatRequest(new CreateSessionRequest { Model = model, Mode = mode }, prompt),
            terminal);
        return await enhancedChat.Cli.Run(cancellationToken).ConfigureAwait(false);
    }
}
