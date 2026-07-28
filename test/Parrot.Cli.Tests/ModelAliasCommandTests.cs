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

        await new ModelAliasCommand(client, new ModelWizard(client, dialog), dialog).Run(cancellationToken);

        _ = await Assert.That(invoker.ConfiguredAliases).HasSingleItem();
        _ = await Assert.That(invoker.ConfiguredAliases[0].Name).IsEqualTo("high_llm");
        _ = await Assert.That(invoker.ConfiguredAliases[0].ModelString).IsEqualTo("provider/model/high");
        _ = await Assert.That(invoker.Updated).IsEmpty();
        _ = await Assert.That(dialog.Shown).Contains("Model alias configured: high_llm = provider/model/high");
        _ = await Assert.That(dialog.Pickers[0].Options[0].Description)
            .IsEqualTo("General purpose; not configured");
        _ = await Assert.That(string.Join('|', dialog.Pickers.Select(picker => picker.Title)))
            .IsEqualTo("Select a model alias|Select a provider|Select a model|Select model effort");
    }

    [Test]
    public async Task Wizard_uses_current_target_detail_and_reports_empty_alias_catalog(
        CancellationToken cancellationToken)
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
            .Run(cancellationToken);

        _ = await Assert.That(invoker.ConfiguredAliases[0].ModelString).IsEqualTo("provider/model");
        _ = await Assert.That(selectedDialog.Pickers[0].Options[0].Id).IsEqualTo("low_llm");
        _ = await Assert.That(selectedDialog.Pickers[0].Options[0].Description)
            .IsEqualTo("Routine work; provider/old/low");

        var emptyInvoker = new ScriptedInvoker();
        var emptyClient = new GeneratedParrot.ParrotClient(emptyInvoker);
        var emptyDialog = new TestSlashDialog();
        await new ModelAliasCommand(emptyClient, new ModelWizard(emptyClient, emptyDialog), emptyDialog)
            .Run(cancellationToken);

        _ = await Assert.That(string.Join('|', emptyDialog.Errors)).IsEqualTo("no model aliases configured");
        _ = await Assert.That(emptyInvoker.ConfiguredAliases).IsEmpty();
    }
}
