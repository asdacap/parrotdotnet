using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ModelPresetCommandTests
{
    [Test]
    public async Task Commands_save_and_select_exact_named_alias_preset(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("high_llm")
        {
            Preset = new ModelPreset { Name = "Work", Model = "high_llm" },
        };
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        await new ModelPresetSetCommand(session, dialog).Run("Work", cancellationToken);
        await new ModelPresetSetCommand(session, dialog).Run("Work", cancellationToken);
        await new ModelPresetSelectCommand(session, activity, dialog).Run("Work", cancellationToken);

        _ = await Assert.That(string.Join(',', session.SetModelPresets)).IsEqualTo("Work,Work");
        _ = await Assert.That(string.Join(',', session.SelectedModelPresets)).IsEqualTo("Work");
        _ = await Assert.That(session.Model).IsEqualTo("high_llm");
        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(dialog.Shown).Contains("Model preset saved: Work = high_llm");
        _ = await Assert.That(dialog.Shown).Contains("Model preset selected: Work = high_llm");
    }

    [Test]
    [Arguments("")]
    [Arguments("two words")]
    [Arguments("bad/name")]
    [Arguments("line\nbreak")]
    public async Task Commands_reject_invalid_names_without_waiting_or_mutating(
        string name,
        CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        await new ModelPresetSetCommand(session, dialog).Run(name, cancellationToken);
        await new ModelPresetSelectCommand(session, activity, dialog).Run(name, cancellationToken);

        _ = await Assert.That(session.SetModelPresets).IsEmpty();
        _ = await Assert.That(session.SelectedModelPresets).IsEmpty();
        _ = await Assert.That(activity.Waits).IsEqualTo(0);
        _ = await Assert.That(dialog.Errors).Count().IsEqualTo(2);
        _ = await Assert.That(dialog.Errors[0]).StartsWith("usage: /model-preset-set <name>");
        _ = await Assert.That(dialog.Errors[1]).StartsWith("usage: /model-preset-select <name>");
    }

    [Test]
    public async Task Commands_report_server_errors_and_cancel_before_rpc(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker { ModelPresetFailure = StatusCode.NotFound };
        var client = new GeneratedParrot.ParrotClient(invoker);
        var session = new SlashSession(
            client,
            new UserSession { Id = "session-7", Model = "provider/model", Mode = "build" },
            new Parrot.Config.Configuration(Path.Combine(Path.GetTempPath(), $"parrot-{Guid.NewGuid():N}.yaml")),
            false,
            new RecordingSlashSessionBinding());
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        await new ModelPresetSetCommand(session, dialog).Run("missing", cancellationToken);
        await new ModelPresetSelectCommand(session, activity, dialog).Run("missing", cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Errors)).IsEqualTo(
            "scripted model preset failure|scripted model preset failure");
        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(invoker.SetModelPresets).IsEmpty();
        _ = await Assert.That(invoker.SelectedModelPresets).IsEmpty();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _ = await Assert.That(async () => await new ModelPresetSetCommand(session, dialog)
            .Run("work", cancelled.Token)).Throws<OperationCanceledException>();
        _ = await Assert.That(async () => await new ModelPresetSelectCommand(session, activity, dialog)
            .Run("work", cancelled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Slash_session_uses_authoritative_response_without_writing_client_config(
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"parrot-{Guid.NewGuid():N}.yaml");
        var configuration = new Parrot.Config.Configuration(configPath);
        var invoker = new ScriptedInvoker { SelectedModelPresetModel = "high_llm" };
        invoker.ModelPreset.Name = "Work";
        invoker.ModelPreset.Model = "high_llm";
        var session = new SlashSession(
            new GeneratedParrot.ParrotClient(invoker),
            new UserSession { Id = "session-7", Model = "provider/old", Mode = "build" },
            configuration,
            false,
            new RecordingSlashSessionBinding());

        var saved = await session.SetModelPreset("Work", cancellationToken);
        var selected = await session.SelectModelPreset("Work", cancellationToken);

        _ = await Assert.That(saved.Model).IsEqualTo("high_llm");
        _ = await Assert.That(selected.Model).IsEqualTo("high_llm");
        _ = await Assert.That(session.Model).IsEqualTo("high_llm");
        _ = await Assert.That(invoker.SetModelPresets.Single().UserSessionId).IsEqualTo("session-7");
        _ = await Assert.That(invoker.SelectedModelPresets.Single().Name).IsEqualTo("Work");
        _ = await Assert.That(File.Exists(configPath)).IsFalse();
    }

    private sealed class RecordingSlashSessionBinding : ISlashSessionBinding
    {
        public Task Replace(UserSession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
