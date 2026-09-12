using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class SkillsCommandTests
{
    [Test]
    public async Task Command_lists_and_repeatedly_toggles_exact_duplicate_paths(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        session.Skills.Skills.AddRange(
        [
            new Skill
            {
                Name = "same",
                Description = "same description",
                Path = "/repo/one/SKILL.md",
                Enabled = true,
                Scope = ListedSkillScope.SkillScopeRepo,
                DisplayName = "Repo Same",
                ShortDescription = "repo short",
            },
            new Skill
            {
                Name = "same",
                Description = "same description",
                Path = "/user/two/SKILL.md",
                Enabled = false,
                Scope = ListedSkillScope.SkillScopeUser,
            },
        ]);
        session.Skills.Errors.Add(new SkillLoadError { Path = "/bad/SKILL.md", Message = "invalid frontmatter" });
        var activity = new TestSlashActivity();
        var refreshes = 0;
        var listDialog = new TestSlashDialog().Select("list");
        ISlashCommand listCommand = new SkillsCommand(session, activity, listDialog, _ => Task.CompletedTask);
        await listCommand.Run(string.Empty, cancellationToken);

        var manageDialog = new TestSlashDialog().Select("manage", "/user/two/SKILL.md", "/repo/one/SKILL.md", null);
        ISlashCommand manageCommand = new SkillsCommand(
            session,
            activity,
            manageDialog,
            _ =>
            {
                refreshes++;
                return Task.CompletedTask;
            });
        await manageCommand.Run(string.Empty, cancellationToken);

        _ = await Assert.That(string.Join('\n', listDialog.Shown)).Contains("[x] Repo Same (same) — repo short — repo — /repo/one/SKILL.md");
        _ = await Assert.That(string.Join('\n', listDialog.Shown)).Contains("[ ] same — ");
        _ = await Assert.That(string.Join('\n', listDialog.Shown)).Contains("Error: /bad/SKILL.md — invalid frontmatter");
        _ = await Assert.That(string.Join('|', session.ConfiguredSkills.Select(request => request.Path)))
            .IsEqualTo("/user/two/SKILL.md|/repo/one/SKILL.md");
        _ = await Assert.That(string.Join('|', session.ConfiguredSkills.Select(request => request.Enabled)))
            .IsEqualTo("True|False");
        _ = await Assert.That(activity.Waits).IsEqualTo(2);
        _ = await Assert.That(refreshes).IsEqualTo(2);
        _ = await Assert.That(manageDialog.Pickers[1].Options.Select(option => option.Id)).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Command_handles_usage_empty_cancellation_and_rpc_errors(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var dialog = new TestSlashDialog();
        ISlashCommand command = new SkillsCommand(session, new TestSlashActivity(), dialog, _ => Task.CompletedTask);

        await command.Run("extra", cancellationToken);
        _ = dialog.Select("list");
        await command.Run(string.Empty, cancellationToken);
        _ = dialog.Select((string?)null);
        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Errors)).IsEqualTo("usage: /skills");
        _ = await Assert.That(string.Join('|', dialog.Shown)).IsEqualTo("No skills available.");
        _ = await Assert.That(session.ConfiguredSkills).IsEmpty();

        ISlashSession failure = new FailingSlashSession();
        var failureDialog = new TestSlashDialog().Select("list");
        ISlashCommand failureCommand = new SkillsCommand(failure, new TestSlashActivity(), failureDialog, _ => Task.CompletedTask);
        await failureCommand.Run(string.Empty, cancellationToken);
        _ = await Assert.That(string.Join('|', failureDialog.Errors)).IsEqualTo("skills unavailable: unavailable");
    }

    private sealed class FailingSlashSession : ISlashSession
    {
        public string Id => "session";

        public string Model => "model";

        public string Mode => "build";

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

        public Task Compact(string? targetContextSize, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SetContextLimitResponse> SetContextLimit(string contextLimit, CancellationToken cancellationToken) =>
            Task.FromResult(new SetContextLimitResponse { ContextLimit = contextLimit });

        public Task<ListSkillsResponse> ListSkills(CancellationToken cancellationToken) =>
            Task.FromException<ListSkillsResponse>(new RpcException(new Status(StatusCode.Unavailable, "unavailable")));

        public Task<Skill> ConfigureSkill(string path, bool enabled, CancellationToken cancellationToken) =>
            Task.FromException<Skill>(new RpcException(new Status(StatusCode.Unavailable, "unavailable")));
    }
}
