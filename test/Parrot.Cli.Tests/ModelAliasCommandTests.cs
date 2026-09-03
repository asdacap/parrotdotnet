using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ModelAliasCommandTests
{
    [Test]
    public async Task Wizard_configures_only_alias_with_explicit_effort(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "high_llm",
            Usage = "General purpose",
        });
        invoker.Models.Clear();
        invoker.Models.Add(new Model
        {
            ProviderId = "provider",
            Id = "model",
            Variants =
            {
                new ModelVariant { Name = "low", ReasoningEffort = "minimal" },
                new ModelVariant { Name = "high", ReasoningEffort = "xhigh" },
            },
        });
        var client = new GeneratedParrot.ParrotClient(invoker);
        var dialog = new TestSlashDialog().Select("high_llm", "provider", "model", "high");

        await new ModelAliasCommand(client, new ModelWizard(client, dialog), dialog).Run(string.Empty, cancellationToken);

        _ = await Assert.That(invoker.ConfiguredAliases).HasSingleItem();
        _ = await Assert.That(invoker.ConfiguredAliases[0].Name).IsEqualTo("high_llm");
        _ = await Assert.That(invoker.ConfiguredAliases[0].ModelString).IsEqualTo("provider/model/high");
        _ = await Assert.That(invoker.Updated).IsEmpty();
        _ = await Assert.That(dialog.Shown).Contains("Model alias configured: high_llm = provider/model/high");
        _ = await Assert.That(dialog.Pickers[0].Options[0].Id).IsEqualTo("/provider-defaults");
        _ = await Assert.That(dialog.Pickers[0].Options[0].Label).IsEqualTo("Use provider defaults");
        _ = await Assert.That(dialog.Pickers[0].Options[0].Description)
            .IsEqualTo("Configure all four model aliases");
        _ = await Assert.That(dialog.Pickers[0].Options[1].Description)
            .IsEqualTo("General purpose; not configured");
        _ = await Assert.That(string.Join('|', dialog.Pickers.Select(picker => picker.Title)))
            .IsEqualTo("Select a model alias|Select a provider|Select a model|Select model effort");
    }

    [Test]
    public async Task Wizard_uses_current_target_detail(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "z_llm",
            ModelString = "provider/old/high",
            Usage = "Last work",
        });
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "low_llm",
            ModelString = "provider/old/low",
            Usage = "Routine work",
        });
        var client = new GeneratedParrot.ParrotClient(invoker);
        var selectedDialog = new TestSlashDialog().Select("low_llm", "provider", "model");

        await new ModelAliasCommand(client, new ModelWizard(client, selectedDialog), selectedDialog)
            .Run(string.Empty, cancellationToken);

        _ = await Assert.That(invoker.ConfiguredAliases[0].ModelString).IsEqualTo("provider/model");
        _ = await Assert.That(selectedDialog.Pickers[0].Options[1].Id).IsEqualTo("low_llm");
        _ = await Assert.That(selectedDialog.Pickers[0].Options[1].Description)
            .IsEqualTo("Routine work; provider/old/low");
    }

    [Test]
    public async Task Wizard_applies_selected_provider_defaults(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.ProviderModelAliasDefaults.Add(new ProviderModelAliasDefaults { ProviderId = "z-provider" });
        invoker.ProviderModelAliasDefaults.Add(new ProviderModelAliasDefaults { ProviderId = "a-provider" });
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "low_llm",
            ModelString = "aprov/low-model",
            Usage = "mechanical work",
        });
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "medium_llm",
            ModelString = "aprov/med-model",
            Usage = "component work",
        });
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "high_llm",
            ModelString = "aprov/high-model",
            Usage = "general purpose",
        });
        invoker.ModelAliases.Add(new ModelAlias
        {
            Name = "xhigh_llm",
            ModelString = "aprov/xhigh-model",
            Usage = "strategic work",
        });
        var client = new GeneratedParrot.ParrotClient(invoker);
        var dialog = new TestSlashDialog().Select("/provider-defaults", "a-provider");

        await new ModelAliasCommand(client, new ModelWizard(client, dialog), dialog).Run(string.Empty, cancellationToken);

        _ = await Assert.That(invoker.AppliedProviderModelAliasDefaults).HasSingleItem();
        _ = await Assert.That(invoker.AppliedProviderModelAliasDefaults[0].ProviderId).IsEqualTo("a-provider");
        _ = await Assert.That(invoker.ConfiguredAliases).IsEmpty();
        _ = await Assert.That(dialog.Shown).Count().IsEqualTo(5);
        _ = await Assert.That(dialog.Shown[0]).IsEqualTo("Model aliases configured from a-provider defaults:");
        _ = await Assert.That(dialog.Shown[1]).IsEqualTo("  high_llm = aprov/high-model");
        _ = await Assert.That(dialog.Shown[2]).IsEqualTo("  low_llm = aprov/low-model");
        _ = await Assert.That(dialog.Shown[3]).IsEqualTo("  medium_llm = aprov/med-model");
        _ = await Assert.That(dialog.Shown[4]).IsEqualTo("  xhigh_llm = aprov/xhigh-model");
        _ = await Assert.That(dialog.Pickers[1].Title).IsEqualTo("Select provider defaults");
        _ = await Assert.That(string.Join('|', dialog.Pickers[1].Options.Select(option => option.Id)))
            .IsEqualTo("a-provider|z-provider");
    }

    [Test]
    public async Task Wizard_reports_when_no_provider_defaults_are_available(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        var client = new GeneratedParrot.ParrotClient(invoker);
        var dialog = new TestSlashDialog().Select("/provider-defaults");

        await new ModelAliasCommand(client, new ModelWizard(client, dialog), dialog).Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Errors).Contains("no available providers have model alias defaults");
        _ = await Assert.That(invoker.AppliedProviderModelAliasDefaults).IsEmpty();
    }
}
