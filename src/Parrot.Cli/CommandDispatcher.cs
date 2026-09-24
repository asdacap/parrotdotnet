using System.Diagnostics;
using System.Globalization;
using Grpc.Core;
using Parrot.Agent;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Web;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;
using YamlDotNet.Core;

using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class CommandDispatcher(
    Interrupts interrupts,
    TextWriter output,
    TextWriter error,
    ProviderHttpClientCatalog httpClients,
    IBrowserOpener browserOpener,
    IOAuthClient oauthClient,
    ModelsDevInformationProvider modelsDev,
    DiagnosticLogs diagnostics)
{
    public const int ExitSuccess = 0;
    public const int ExitUsage = 2;
    public const int ExitFailure = 1;

    private const string DefaultModel = "opencode-go/glm-5.2";

    private const ushort DefaultWebPort = 7420;

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
          chat --connect <address>    Connect through unix:/path, http, or https
          serve [--listen <address>]  Host on the owner-only default Unix socket
          web [--port <n>] [--token-file <path>]
                                      Serve the browser UI on 127.0.0.1:<n>

        TCP listen/connect requires --token-file <owner-only-file>. Plaintext
        non-loopback listen also requires --unsafe-allow-external.

        --basic forces the minimal renderer; the default is the enhanced one.
        --variant is a deprecated, nonpersistent reasoning-variant override.

        Bare `parrot` is `parrot chat`. In a terminal that opens a REPL; with a
        prompt or piped stdin it answers once. /help lists the slash commands.
        Local chat loads or joins the workspace's last opened session. Failed
        connection attempts are reported before creating a fresh session.
        Existing sessions retain their model and mode; flags configure fresh ones.
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

            case "web":
                return await RunWeb(arguments, cancellationToken).ConfigureAwait(false);

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

    internal static string CliUtilityWarning(CliUtilityAvailability availability) =>
        availability.MissingExpected.Count == 0
            ? string.Empty
            : "warning: expected CLI utilities are unavailable: "
                + string.Join(", ", availability.MissingExpected)
                + "; Bash shell commands may fail";

    private async Task WarnAboutMissingCliUtilities(
        CliUtilityAvailability availability,
        CancellationToken cancellationToken)
    {
        var warning = CliUtilityWarning(availability);
        if (warning.Length > 0)
        {
            await error.WriteLineAsync(warning.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

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

            await AuthFlows.StoreApiKey(store, provider, key, diagnostics.Global, cancellationToken).ConfigureAwait(false);
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
                        diagnostics.Global,
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
        var listed = new SessionCatalog(paths).List();

        foreach (var session in listed
            .OrderBy(item => item.CreatedAt, StringComparer.Ordinal)
            .ThenBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            var rendered = session.State == SessionCatalogState.Corrupt
                ? $"{session.Id.Value}  <corrupt>"
                : $"{session.Id.Value}  {session.Model}  {session.WorkingDirectory}";
            await output.WriteLineAsync(rendered.AsMemory(), cancellationToken).ConfigureAwait(false);
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
        await using var composition = await BuildComposition(credentials, configuration, new UnexposedUserSessionHost(), cancellationToken)
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
        IUserSessionHost sessionHost,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            diagnostics.Global.Write(new DiagnosticEvent("startup", "provider_catalog_start", DiagnosticSeverity.Information)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            var registry = await new ProviderRegistryBuilder(configuration, credentials, httpClients, browserOpener, modelsDev)
                .Build(cancellationToken).ConfigureAwait(false);

            diagnostics.Global.Write(new DiagnosticEvent("startup", "provider_catalog_complete", DiagnosticSeverity.Information)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            return new Composition(
                registry, configuration, Directory.GetCurrentDirectory(), Environment.MachineName, sessionHost, diagnostics);
        }
        catch (LLMProviderException failure)
        {
            diagnostics.Global.Write(new DiagnosticEvent("startup", "provider_catalog_failure", DiagnosticSeverity.Error)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception failure)
        {
            diagnostics.Global.Write(new DiagnosticEvent(
                "startup", "provider_catalog_failure", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    private async Task<int> RunServer(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var paths = StatePaths.ResolveFromEnvironment();
        var listen = $"unix:{Path.Combine(paths.Control, "parrot.sock")}";
        var tokenFile = string.Empty;
        var unsafeExternal = false;

        for (var index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--listen" when index + 1 < arguments.Count:
                    listen = arguments[++index];
                    break;

                case "--token-file" when index + 1 < arguments.Count:
                    tokenFile = arguments[++index];
                    break;

                case "--unsafe-allow-external":
                    unsafeExternal = true;
                    break;

                default:
                    var usage = "usage: parrot serve "
                        + "[--listen unix:/path|http://host:port|https://host:port] "
                        + "[--token-file <path>] [--unsafe-allow-external]";
                    await error.WriteLineAsync(usage.AsMemory(), cancellationToken).ConfigureAwait(false);
                    return ExitUsage;
            }
        }

        TransportAddress address;
        TransportToken? token;
        try
        {
            address = TransportAddress.Parse(listen);
            token = tokenFile.Length > 0 ? TransportToken.Load(tokenFile) : null;
            if (address.IsTcp && token is null)
            {
                throw new InvalidOperationException("TCP listen requires --token-file <owner-only-file>");
            }

            if (address.IsExternal && address.Kind == TransportAddressKind.Http && !unsafeExternal)
            {
                throw new InvalidOperationException("plaintext non-loopback listen requires --unsafe-allow-external");
            }
        }
        catch (InvalidOperationException failure)
        {
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitUsage;
        }

        var configuration = await LoadConfiguration(cancellationToken).ConfigureAwait(false);

        if (configuration is null)
        {
            return ExitFailure;
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        await using var composition = await BuildComposition(credentials, configuration, new UnexposedUserSessionHost(), cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        await WarnAboutMissingCliUtilities(composition.CliUtilities, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var server = await GrpcServer.Start(composition.Service, address, token, diagnostics.Global, cancellationToken)
                .ConfigureAwait(false);
            if (address.IsExternal && address.Kind == TransportAddressKind.Http)
            {
                var warning = "parrot: warning: serving bearer-authenticated control traffic "
                    + "over external plaintext HTTP";
                await error.WriteLineAsync(warning.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync(
                $"parrot serving on {address.Value} (ctrl-c to stop)".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return await server.Run(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException failure)
        {
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitFailure;
        }
    }

    private async Task<int> RunWeb(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var port = DefaultWebPort;
        var tokenFile = string.Empty;

        for (var index = 1; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--port" when index + 1 < arguments.Count
                    && ushort.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed):
                    port = parsed;
                    index++;
                    break;

                case "--token-file" when index + 1 < arguments.Count:
                    tokenFile = arguments[++index];
                    break;

                default:
                    await error.WriteLineAsync("usage: parrot web [--port <n>] [--token-file <path>]".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return ExitUsage;
            }
        }

        TransportToken token;
        try
        {
            token = tokenFile.Length > 0 ? TransportToken.Load(tokenFile) : TransportToken.Generate();
        }
        catch (InvalidOperationException failure)
        {
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitUsage;
        }

        var configuration = await LoadConfiguration(cancellationToken).ConfigureAwait(false);

        if (configuration is null)
        {
            return ExitFailure;
        }

        using var credentials = new FileCredentialStore(StatePaths.ResolveFromEnvironment().CredentialsFile);
        await using var composition = await BuildComposition(credentials, configuration, new LocalUserSessionHost(), cancellationToken)
            .ConfigureAwait(false);

        if (composition is null)
        {
            return ExitFailure;
        }

        await WarnAboutMissingCliUtilities(composition.CliUtilities, cancellationToken).ConfigureAwait(false);
        using var applicationExit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var slashService = new WebSlashService(
            new GeneratedParrot.ParrotClient(new InProcessCallInvoker(composition.Service)),
            configuration,
            Directory.GetCurrentDirectory(),
            applicationExit,
            credentials,
            oauthClient,
            ProviderRegistryBuilder.BuildableProviderIds(configuration),
            diagnostics.Global);
        try
        {
            await using var server = await WebServer.Start(composition.Service, slashService, port, token, cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                $"parrot web on {server.Addresses.Single()}/#token={token.Bearer} (ctrl-c to stop)".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return await server.Run(applicationExit.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException failure)
        {
            await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return ExitFailure;
        }
    }

    private async Task<Configuration?> LoadConfiguration(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var correlationId = Guid.NewGuid().ToString("N");
        try
        {
            var paths = StatePaths.ResolveFromEnvironment();
            diagnostics.Global.Write(new DiagnosticEvent("startup", "configuration_start", DiagnosticSeverity.Information)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
            diagnostics.Global.Write(new DiagnosticEvent("startup", "configuration_complete", DiagnosticSeverity.Information)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            return configuration;
        }
        catch (Exception failure) when (failure is InvalidDataException or YamlException)
        {
            diagnostics.Global.Write(new DiagnosticEvent("startup", "configuration_failure", DiagnosticSeverity.Error)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            await error.WriteLineAsync($"parrot: invalid configuration: {failure.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception failure)
        {
            diagnostics.Global.Write(new DiagnosticEvent(
                "startup", "configuration_failure", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = correlationId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    private async Task<int> RunChat(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var paths = StatePaths.ResolveFromEnvironment();
        var model = DefaultModel;
        var modelOverridden = false;
        var mode = string.Empty;
        var connect = string.Empty;
        var tokenFile = string.Empty;
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

                case "--token-file" when index + 1 < arguments.Count:
                    tokenFile = arguments[++index];
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
        mode = mode.Length == 0 ? configuration.DefaultProfile : mode;
        var prompt = string.Join(' ', words);
        var attachmentProfiles = new ProfileRegistry(
            configuration.Profiles,
            configuration.SandboxRules,
            [],
            configuration.DisabledTools);
        var attachmentModes = new ModeRegistry(attachmentProfiles, configuration.DefaultProfile);
        var attachments = new PromptAttachmentUploader(new ToolWorkspace(Directory.GetCurrentDirectory()), attachmentModes);

        // Remote: the server owns the provider, the state, and the tools; this
        // process is only a client of the same contract (principle 11).
        if (connect.Length > 0)
        {
            TransportAddress address;
            TransportToken? token;
            try
            {
                address = TransportAddress.Parse(connect);
                token = tokenFile.Length > 0 ? TransportToken.Load(tokenFile) : null;
                if (address.IsTcp && token is null)
                {
                    throw new InvalidOperationException("TCP transport requires --token-file <owner-only-file>");
                }
            }
            catch (InvalidOperationException failure)
            {
                await error.WriteLineAsync($"parrot: {failure.Message}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ExitUsage;
            }

            using var remoteConnection = GrpcTransportClient.Connect(address, token, diagnostics.Global);
            var remote = remoteConnection.Client;
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
                    error,
                    attachments,
                    diagnostics.Global);
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
                    error,
                    attachments,
                    diagnostics.Global);
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
                remoteTerminal,
                attachments,
                diagnostics.Global);
            return await remoteChat.Cli.Run(cancellationToken).ConfigureAwait(false);
        }

        if (prompt.Length == 0 && Console.IsInputRedirected)
        {
            prompt = (await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (prompt.Length == 0)
            {
                return ExitUsage;
            }
        }

        using var credentials = new FileCredentialStore(paths.CredentialsFile);
        Composition? composition = null;
        async Task<GeneratedParrot.ParrotClient> OpenLocalClient(CancellationToken token)
        {
            composition = await BuildComposition(credentials, configuration, new LocalUserSessionHost(), token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("cannot initialize local providers");
            await WarnAboutMissingCliUtilities(composition.CliUtilities, token).ConfigureAwait(false);
            return new GeneratedParrot.ParrotClient(new InProcessCallInvoker(composition.Service));
        }

        var invalidVariant = false;
        async Task<CreateSessionRequest> ConfigureFresh(GeneratedParrot.ParrotClient freshClient, CancellationToken token)
        {
            if (variant is not null)
            {
                var overridden = await OverrideVariant(freshClient, model, variant, error, token).ConfigureAwait(false);
                invalidVariant = overridden is null;
                model = overridden ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid reasoning variant"));
            }

            return new CreateSessionRequest { Model = model, Mode = mode };
        }

        using var startup = new LocalChatStartup(
            paths, Directory.GetCurrentDirectory(), Environment.MachineName, error, diagnostics.Global, OpenLocalClient, ConfigureFresh);
        try
        {
            var (client, initialSession) = await startup.Open(prompt.Length == 0, cancellationToken).ConfigureAwait(false);
            model = initialSession.Model;
            mode = initialSession.Mode;
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
                    error,
                    attachments,
                    diagnostics.Global)
                { InitialSession = initialSession };
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
                    error,
                    attachments,
                    diagnostics.Global)
                { InitialSession = initialSession };
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
                new EnhancedChatRequest(new CreateSessionRequest { Model = model, Mode = mode }, prompt)
                {
                    InitialSession = initialSession,
                },
                terminal,
                attachments,
                diagnostics.Global);
            return await enhancedChat.Cli.Run(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is InvalidOperationException or RpcException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (invalidVariant)
            {
                return ExitUsage;
            }

            var message = failure is RpcException rpcFailure ? rpcFailure.Status.Detail : failure.Message;
            await error.WriteLineAsync($"parrot: {message}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitFailure;
        }
        finally
        {
            if (composition is not null)
            {
                await composition.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
