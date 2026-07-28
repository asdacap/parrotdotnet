using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ModelCommandTests : IDisposable
{
    private readonly string _configurationPath = Path.Combine(
        Path.GetTempPath(), $"parrot-model-command-tests-{Guid.NewGuid():N}.yaml");

    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly HttpClient _http = new();

    [Test]
    [Arguments("provider/current/high", "provider/next", "provider/next/high")]
    [Arguments("provider/current/high", "provider/other", "provider/other/low")]
    [Arguments("provider/current/high", "provider/plain", "provider/plain")]
    [Arguments("provider/current/high", "provider/next/low", "provider/next/low")]
    public async Task Model_switch_applies_variant_rules_and_persists_after_success(
        string current,
        string requested,
        string expected,
        CancellationToken cancellationToken)
    {
        var invoker = InvokerWithModels();
        using var input = new StringReader(string.Empty);
        var configuration = new Configuration(_configurationPath);
        var context = Context(invoker, configuration, input, current);

        _ = await new ModelCommand().Run(context, requested, cancellationToken);

        _ = await Assert.That(invoker.Updated).HasSingleItem();
        _ = await Assert.That(invoker.Updated[0].Model).IsEqualTo(expected);
        _ = await Assert.That(context.Model).IsEqualTo(expected);
        _ = await Assert.That(configuration.Model).IsEqualTo(expected);
        _ = await Assert.That(Configuration.Load(_configurationPath).Model).IsEqualTo(expected);
    }

    [Test]
    public async Task Effort_direct_and_prompted_selection_use_metadata_order_and_persist(
        CancellationToken cancellationToken)
    {
        var directInvoker = InvokerWithModels();
        var directConfiguration = new Configuration(_configurationPath);
        using var directInput = new StringReader(string.Empty);
        var direct = Context(directInvoker, directConfiguration, directInput, "provider/current/low");

        _ = await new EffortCommand().Run(direct, "high", cancellationToken);

        _ = await Assert.That(directInvoker.Updated).HasSingleItem();
        _ = await Assert.That(directInvoker.Updated[0].Model).IsEqualTo("provider/current/high");
        _ = await Assert.That(direct.Model).IsEqualTo("provider/current/high");
        _ = await Assert.That(directConfiguration.Model).IsEqualTo("provider/current/high");
        _ = await Assert.That(_output.ToString()).Contains("Model effort selected");

        var promptedInvoker = InvokerWithModels();
        var promptedConfiguration = new Configuration(_configurationPath);
        using var promptedInput = new StringReader("low\n");
        var prompted = Context(promptedInvoker, promptedConfiguration, promptedInput, "provider/current/high");

        _ = await new EffortCommand().Run(prompted, string.Empty, cancellationToken);

        _ = await Assert.That(promptedInvoker.Updated).HasSingleItem();
        _ = await Assert.That(promptedInvoker.Updated[0].Model).IsEqualTo("provider/current/low");
        _ = await Assert.That(prompted.Model).IsEqualTo("provider/current/low");
        _ = await Assert.That(_output.ToString()).Contains("  low").And.Contains("  high").And.Contains("effort> ");
    }

    [Test]
    [Arguments("missing", "unknown model effort missing")]
    [Arguments("HIGH", "unknown model effort HIGH")]
    public async Task Invalid_effort_does_not_update_or_persist(
        string effort, string expectedError, CancellationToken cancellationToken)
    {
        var invoker = InvokerWithModels();
        var configuration = new Configuration(_configurationPath);
        using var input = new StringReader(string.Empty);
        var context = Context(invoker, configuration, input, "provider/current/low");

        _ = await new EffortCommand().Run(context, effort, cancellationToken);

        _ = await Assert.That(invoker.Updated).IsEmpty();
        _ = await Assert.That(context.Model).IsEqualTo("provider/current/low");
        _ = await Assert.That(configuration.Model).IsEmpty();
        _ = await Assert.That(_error.ToString()).Contains(expectedError).And.Contains("low, high");
    }

    [Test]
    public async Task Startup_variant_override_replaces_existing_suffix_and_validates_metadata(
        CancellationToken cancellationToken)
    {
        var invoker = InvokerWithModels();
        var client = new GeneratedParrot.ParrotClient(invoker);

        var selected = await CommandDispatcher.OverrideVariant(
            client, "provider/current/low", "high", _error, cancellationToken);
        var invalid = await CommandDispatcher.OverrideVariant(
            client, "provider/current/high", "missing", _error, cancellationToken);
        var unknown = await CommandDispatcher.OverrideVariant(
            client, "provider/unknown", "high", _error, cancellationToken);

        _ = await Assert.That(selected).IsEqualTo("provider/current/high");
        _ = await Assert.That(invalid).IsNull();
        _ = await Assert.That(unknown).IsNull();
        _ = await Assert.That(_error.ToString()).Contains("does not support effort missing")
            .And.Contains("unknown model selection provider/unknown");
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _http.Dispose();
        File.Delete(_configurationPath);
        File.Delete(_configurationPath + ".tmp");
    }

    private static ScriptedInvoker InvokerWithModels()
    {
        var invoker = new ScriptedInvoker();
        invoker.AddModel(Model("current", ("low", "low"), ("high", "xhigh")));
        invoker.AddModel(Model("next", ("low", "low"), ("high", "high")));
        invoker.AddModel(Model("other", ("low", "minimal")));
        invoker.AddModel(Model("plain"));
        return invoker;
    }

    private static Model Model(string id, params (string Name, string Effort)[] variants)
    {
        var model = new Model { ProviderId = "provider", Id = id };
        model.Variants.AddRange(variants.Select(
            variant => new ModelVariant { Name = variant.Name, ReasoningEffort = variant.Effort }));
        return model;
    }

    private SlashContext Context(
        ScriptedInvoker invoker,
        Configuration configuration,
        TextReader input,
        string model) =>
        new(
            new GeneratedParrot.ParrotClient(invoker),
            new UnusedCredentials(),
            new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            configuration,
            ["provider"],
            "user-session",
            model,
            "build",
            new ConsolePromptReader(input),
            _output,
            _error);
}
