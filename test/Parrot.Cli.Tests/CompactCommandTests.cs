using Parrot.Cli.Commands;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class CompactCommandTests
{
    [Test]
    public async Task Bare_compact_waits_for_idle_and_compacts_once_with_the_same_token(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        ISlashCommand command = new CompactCommand(session, activity, dialog);
        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(activity.CancellationToken).IsEqualTo(cancellationToken);
        _ = await Assert.That(session.Compactions).IsEqualTo(1);
        _ = await Assert.That(session.CompactionCancellationToken).IsEqualTo(cancellationToken);
        _ = await Assert.That(dialog.Errors).IsEmpty();
    }

    [Test]
    [Arguments("requested history")]
    [Arguments(" ")]
    public async Task Arguments_are_rejected_without_waiting_or_compacting(string arguments, CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        ISlashCommand command = new CompactCommand(session, activity, dialog);
        await command.Run(arguments, cancellationToken);

        _ = await Assert.That(activity.Waits).IsEqualTo(0);
        _ = await Assert.That(session.Compactions).IsEqualTo(0);
        _ = await Assert.That(dialog.Errors).Contains("usage: /compact");
    }

    [Test]
    public async Task A_failed_compaction_is_reported_without_failing_the_command(CancellationToken cancellationToken)
    {
        var session = new FailingCompactSession();
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        await new CompactCommand(session, activity, dialog).Run(string.Empty, cancellationToken);

        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(session.Compactions).IsEqualTo(1);
        _ = await Assert.That(dialog.Errors)
            .Contains("compaction failed: The compaction provider did not complete with a summary.");
    }

    private sealed class FailingCompactSession : ISlashSession
    {
        public string Id => "session";

        public string Model => "model";

        public string Mode => "build";

        public int Compactions { get; private set; }

        public Task SelectModel(string model, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ModelPreset> SetModelPreset(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelPreset());

        public Task<ModelPreset> SelectModelPreset(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelPreset());

        public Task<IReadOnlyList<ModelPreset>> ListModelPresets(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelPreset>>([]);

        public Task SelectMode(string mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartNew(string model, string mode, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetGoal(string goal, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearGoal(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Compact(CancellationToken cancellationToken)
        {
            Compactions++;
            return Task.FromException(
                new InvalidOperationException("The compaction provider did not complete with a summary."));
        }

        public Task<ListSkillsResponse> ListSkills(CancellationToken cancellationToken) =>
            Task.FromResult(new ListSkillsResponse());

        public Task<Skill> ConfigureSkill(string path, bool enabled, CancellationToken cancellationToken) =>
            Task.FromResult(new Skill());
    }
}
