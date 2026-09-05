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
            Skill("zebra", "/zebra", true, "zebra description", string.Empty),
            Skill("alpha", "/alpha", true, "alpha description", "short alpha"),
            Skill("alpine", "/alpine", false, "disabled", string.Empty));
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
        _ = await Assert.That(editor.Prompt).IsEqualTo(new PromptValue("> ", "🙂 use $alpha suffix", 12));
        _ = await Assert.That(string.Join('|', completion.Skills.Select(skill => skill.Name))).IsEqualTo("alpha");
        _ = await Assert.That(invoker.SkillLists("session-1")).IsEqualTo(1);

        completion.Refresh(new PromptValue("> ", "word$al", 7));
        _ = await Assert.That(completion.Skills).IsEmpty();
        foreach (var cursor in new[] { 1, 3, 5 })
        {
            completion.Refresh(new PromptValue("> ", "$HOME", cursor));
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
            Skill("beta", "/beta", true, "beta", string.Empty),
            Skill("alpha", "/alpha", true, "alpha", string.Empty),
            Skill("alpha", "/duplicate", true, "duplicate", string.Empty));
        invoker.SetSkills("session-2", Skill("other", "/other", true, "other", string.Empty));
        var completion = new SkillCompletion(new Protocol.Parrot.ParrotClient(invoker));
        await completion.RefreshCatalog("session-1", cancellationToken);
        completion.Refresh(new PromptValue("> ", "$", 1));
        completion.SelectNext();
        completion.Refresh(new PromptValue("> ", "$", 1));

        _ = await Assert.That(string.Join('|', completion.Skills.Select(skill => $"{skill.Name}:{skill.Path}")))
            .IsEqualTo("alpha:/alpha|beta:/beta");
        _ = await Assert.That(completion.Selected).IsEqualTo(1);
        completion.SelectNext();
        _ = await Assert.That(completion.Selected).IsEqualTo(0);
        completion.SelectPrevious();
        _ = await Assert.That(completion.Selected).IsEqualTo(1);

        await completion.RefreshCatalog("session-2", cancellationToken);
        completion.Refresh(new PromptValue("> ", "$", 1));
        _ = await Assert.That(string.Join('|', completion.Skills.Select(skill => skill.Name))).IsEqualTo("other");
        _ = await Assert.That(invoker.SkillLists("session-2")).IsEqualTo(1);
    }

    private static Skill Skill(
        string name,
        string path,
        bool enabled,
        string description,
        string shortDescription)
    {
        var skill = new Skill { Name = name, Path = path, Enabled = enabled, Description = description };
        if (shortDescription.Length > 0)
        {
            skill.ShortDescription = shortDescription;
        }

        return skill;
    }
}
