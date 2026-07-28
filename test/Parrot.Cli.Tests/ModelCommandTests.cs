using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ModelCommandTests : IDisposable
{
    private readonly StringWriter _error = new();

    [Test]
    [Arguments("provider/current/high", "next", "provider/next/high")]
    [Arguments("provider/current/high", "other", "provider/other/low")]
    [Arguments("provider/current/high", "plain", "provider/plain")]
    [Arguments("provider/current/low", "next", "provider/next/low")]
    [Arguments("current_llm", "next", "provider/next/high")]
    public async Task Model_wizard_applies_variant_rules(
        string current,
        string requested,
        string expected,
        CancellationToken cancellationToken)
    {
        var invoker = InvokerWithModels();
        if (string.Equals(current, "current_llm", StringComparison.Ordinal))
        {
            invoker.ModelAliases.Add(new ModelAlias
            {
                Name = current,
                ModelString = "provider/current/high",
                Usage = "current",
            });
        }

        var client = new GeneratedParrot.ParrotClient(invoker);
        var session = new TestSlashSession(current);
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog().Select("provider", requested);

        await new ModelCommand(new ModelWizard(client, dialog), session, activity, dialog)
            .Run(cancellationToken);

        _ = await Assert.That(session.Model).IsEqualTo(expected);
        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(dialog.Shown).Contains($"model is now {expected} (saved)");
    }

    [Test]
    public async Task Effort_uses_metadata_and_cancellation_does_not_update(CancellationToken cancellationToken)
    {
        var invoker = InvokerWithModels();
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "current_llm",
            ModelString = "provider/current/low",
            Usage = "current",
        });
        var client = new GeneratedParrot.ParrotClient(invoker);
        var selectedSession = new TestSlashSession("current_llm");
        var selectedDialog = new TestSlashDialog().Select("high");
        var activity = new TestSlashActivity();

        await new EffortCommand(client, selectedSession, activity, selectedDialog).Run(cancellationToken);

        _ = await Assert.That(selectedSession.Model).IsEqualTo("provider/current/high");
        _ = await Assert.That(selectedDialog.Shown).Contains("Model effort selected: high");

        var cancelledSession = new TestSlashSession("provider/current/high");
        var cancelledDialog = new TestSlashDialog().Select((string?)null);
        await new EffortCommand(client, cancelledSession, activity, cancelledDialog).Run(cancellationToken);

        _ = await Assert.That(cancelledSession.Model).IsEqualTo("provider/current/high");
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
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "high_llm",
            ModelString = "provider/current/low",
            Usage = "general",
        });
        var alias = await CommandDispatcher.OverrideVariant(
            client, "high_llm", "high", _error, cancellationToken);

        _ = await Assert.That(selected).IsEqualTo("provider/current/high");
        _ = await Assert.That(alias).IsEqualTo("provider/current/high");
        _ = await Assert.That(invalid).IsNull();
        _ = await Assert.That(unknown).IsNull();
        _ = await Assert.That(_error.ToString()).Contains("does not support effort missing")
            .And.Contains("unknown model selection provider/unknown");
    }

    public void Dispose() => _error.Dispose();

    private static ScriptedInvoker InvokerWithModels()
    {
        var invoker = new ScriptedInvoker();
        invoker.Models.Clear();
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
}
