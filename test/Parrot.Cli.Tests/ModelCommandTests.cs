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
        var invoker = new ModelsFixture().Invoker;
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
        ISlashSession session = new TestSlashSession(current);
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog().Select("provider", requested);

        ISlashCommand command = new ModelCommand(new ModelWizard(client, dialog), session, activity, dialog);
        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(session.Model).IsEqualTo(expected);
        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(dialog.Shown).Contains($"model is now {expected} (saved)");
    }

    [Test]
    public async Task Effort_uses_metadata_and_cancellation_does_not_update(CancellationToken cancellationToken)
    {
        var invoker = new ModelsFixture().Invoker;
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "current_llm",
            ModelString = "provider/current/low",
            Usage = "current",
        });
        var client = new GeneratedParrot.ParrotClient(invoker);
        ISlashSession selectedSession = new TestSlashSession("current_llm");
        var selectedDialog = new TestSlashDialog().Select("high");
        var activity = new TestSlashActivity();

        ISlashCommand selectedCommand = new EffortCommand(client, selectedSession, activity, selectedDialog);
        await selectedCommand.Run(string.Empty, cancellationToken);

        _ = await Assert.That(selectedSession.Model).IsEqualTo("provider/current/high");
        _ = await Assert.That(selectedDialog.Shown).Contains("Model effort selected: high");

        ISlashSession cancelledSession = new TestSlashSession("provider/current/high");
        var cancelledDialog = new TestSlashDialog().Select((string?)null);
        ISlashCommand cancelledCommand = new EffortCommand(client, cancelledSession, activity, cancelledDialog);
        await cancelledCommand.Run(string.Empty, cancellationToken);

        _ = await Assert.That(cancelledSession.Model).IsEqualTo("provider/current/high");
    }

    [Test]
    public async Task Startup_variant_override_replaces_existing_suffix_and_validates_metadata(
        CancellationToken cancellationToken)
    {
        var invoker = new ModelsFixture().Invoker;
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

    private sealed class ModelsFixture
    {
        public ModelsFixture()
        {
            Invoker.Models.Clear();
            Invoker.AddModel(new Model
            {
                ProviderId = "provider",
                Id = "current",
                Variants =
                {
                    new ModelVariant { Name = "low", ReasoningEffort = "low" },
                    new ModelVariant { Name = "high", ReasoningEffort = "xhigh" },
                },
            });
            Invoker.AddModel(new Model
            {
                ProviderId = "provider",
                Id = "next",
                Variants =
                {
                    new ModelVariant { Name = "low", ReasoningEffort = "low" },
                    new ModelVariant { Name = "high", ReasoningEffort = "high" },
                },
            });
            Invoker.AddModel(new Model
            {
                ProviderId = "provider",
                Id = "other",
                Variants =
                {
                    new ModelVariant { Name = "low", ReasoningEffort = "minimal" },
                },
            });
            Invoker.AddModel(new Model
            {
                ProviderId = "provider",
                Id = "plain",
            });
        }

        public ScriptedInvoker Invoker { get; } = new();
    }
}
