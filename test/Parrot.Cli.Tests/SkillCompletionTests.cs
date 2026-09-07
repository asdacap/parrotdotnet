using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class SkillCompletionTests
{
    [Test]
    public async Task Completion_filters_enabled_skills_and_replaces_only_the_active_rune_token(
        CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.SetSkills(
            "session-1",
            new Skill { Name = "zebra", Path = "/zebra", Enabled = true, Description = "zebra description" },
            new Skill { Name = "alpha", Path = "/alpha", Enabled = true, Description = "alpha description", ShortDescription = "short alpha" },
            new Skill { Name = "alpine", Path = "/alpine", Enabled = false, Description = "disabled" });
        var completion = new SkillCompletion(new Protocol.Parrot.ParrotClient(invoker));
        await completion.RefreshCatalog("session-1", cancellationToken);
        var editor = new IncrementalEditor("> ", 100);
        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Paste, "🙂 use $al suffix"));
        for (var index = 0; index < 7; index++)
        {
            _ = editor.Apply(new TerminalKey(TerminalKeyKind.Left));
        }

        completion.Refresh(editor.Prompt);
        var accepted = completion.Accept(editor);

        _ = await Assert.That(accepted).IsTrue();
        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptState("> ", "🙂 use $alpha suffix", 12));
        _ = await Assert.That(string.Join('|', completion.Skills.Select(skill => skill.Name))).IsEqualTo("alpha");
        _ = await Assert.That(invoker.SkillLists("session-1")).IsEqualTo(1);

        completion.Refresh(new PromptState("> ", "word$al", 7));
        _ = await Assert.That(completion.Skills).IsEmpty();
        foreach (var cursor in new[] { 1, 3, 5 })
        {
            completion.Refresh(new PromptState("> ", "$HOME", cursor));
            _ = await Assert.That(completion.Skills).IsEmpty();
        }
    }

    [Test]
    public async Task Completion_sorts_wraps_preserves_selection_and_refreshes_session_catalog(
        CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.SetSkills(
            "session-1",
            new Skill { Name = "beta", Path = "/beta", Enabled = true, Description = "beta" },
            new Skill { Name = "alpha", Path = "/alpha", Enabled = true, Description = "alpha" },
            new Skill { Name = "alpha", Path = "/duplicate", Enabled = true, Description = "duplicate" });
        invoker.SetSkills("session-2", new Skill { Name = "other", Path = "/other", Enabled = true, Description = "other" });
        var completion = new SkillCompletion(new Protocol.Parrot.ParrotClient(invoker));
        await completion.RefreshCatalog("session-1", cancellationToken);
        completion.Refresh(new PromptState("> ", "$", 1));
        completion.SelectNext();
        completion.Refresh(new PromptState("> ", "$", 1));

        _ = await Assert.That(string.Join('|', completion.Skills.Select(skill => $"{skill.Name}:{skill.Path}")))
            .IsEqualTo("alpha:/alpha|beta:/beta");
        _ = await Assert.That(completion.Selected).IsEqualTo(1);
        completion.SelectNext();
        _ = await Assert.That(completion.Selected).IsEqualTo(0);
        completion.SelectPrevious();
        _ = await Assert.That(completion.Selected).IsEqualTo(1);

        await completion.RefreshCatalog("session-2", cancellationToken);
        completion.Refresh(new PromptState("> ", "$", 1));
        _ = await Assert.That(string.Join('|', completion.Skills.Select(skill => skill.Name))).IsEqualTo("other");
        _ = await Assert.That(invoker.SkillLists("session-2")).IsEqualTo(1);
    }
}
