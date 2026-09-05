using System.Security.Cryptography;
using System.Text;
using Parrot.Skills;

namespace Parrot.Core.Tests;

internal sealed class SkillFoundationTests
{
    [Test]
    public async Task Frontmatter_parses_sanitizes_defaults_and_preserves_content()
    {
        const string content = """
            ---
            description: >-
              A useful
              skill
            metadata:
              short-description: A short description
            ---

            # Instructions
            Keep this text verbatim.
            """;
        var path = Path.Combine(Path.GetTempPath(), "fallback-name", "SKILL.md");

        var parsed = SkillFrontmatterParser.Parse(path, content, SkillScope.User);

        _ = await Assert.That(parsed.Name).IsEqualTo("fallback-name");
        _ = await Assert.That(parsed.Description).IsEqualTo("A useful skill");
        _ = await Assert.That(parsed.ShortDescription).IsEqualTo("A short description");
        _ = await Assert.That(parsed.Path).IsEqualTo(Path.GetFullPath(path));

        var blankName = SkillFrontmatterParser.Parse(
            path,
            "---\nname: '   '\ndescription: valid\n---\nbody",
            SkillScope.User);
        _ = await Assert.That(blankName.Name).IsEqualTo("fallback-name");
    }

    [Test]
    [Arguments("description: valid", 65, false)]
    [Arguments("description: ''", 4, false)]
    [Arguments("description: valid", 64, true)]
    [Arguments("description: " + "a", 4, true)]
    public async Task Frontmatter_enforces_required_bounded_fields(string description, int nameLength, bool valid)
    {
        var name = new string('n', nameLength);
        var content = $"---\nname: {name}\n{description}\n---\nbody";
        var path = Path.Combine(Path.GetTempPath(), "skill", "SKILL.md");

        if (valid)
        {
            var parsed = SkillFrontmatterParser.Parse(path, content, SkillScope.Repo);
            _ = await Assert.That(parsed.Name).IsEqualTo(name);
        }
        else
        {
            _ = await Assert.That(() => SkillFrontmatterParser.Parse(path, content, SkillScope.Repo))
                .Throws<SkillParseException>();
        }
    }

    [Test]
    public async Task Frontmatter_rejects_missing_and_overlong_descriptions()
    {
        var path = Path.Combine(Path.GetTempPath(), "skill", "SKILL.md");
        var missing = "---\nname: valid\n---\nbody";
        var overlong = $"---\nname: valid\ndescription: {new string('d', 1025)}\n---\nbody";

        _ = await Assert.That(() => SkillFrontmatterParser.Parse(path, missing, SkillScope.User))
            .Throws<SkillParseException>();
        _ = await Assert.That(() => SkillFrontmatterParser.Parse(path, overlong, SkillScope.User))
            .Throws<SkillParseException>();
    }

    [Test]
    public async Task Display_metadata_fails_open_and_validates_sanitized_values()
    {
        var valid = SkillDisplayMetadataParser.Parse("""
            dependencies:
              tools: [ignored]
            policy:
              allow_implicit_invocation: false
            interface:
              display_name: "  Display   Name  "
              short_description: >-
                Short
                description
            """);
        var mixed = SkillDisplayMetadataParser.Parse($"""
            interface:
              display_name: {new string('x', 65)}
              short_description: usable
            """);
        var malformed = SkillDisplayMetadataParser.Parse("interface: [");

        _ = await Assert.That(valid).IsEqualTo(new SkillDisplayMetadata("Display Name", "Short description"));
        _ = await Assert.That(mixed).IsEqualTo(new SkillDisplayMetadata(null, "usable"));
        _ = await Assert.That(malformed).IsEqualTo(new SkillDisplayMetadata(null, null));
    }

    [Test]
    public async Task Mentions_and_selection_are_ordered_deduplicated_and_use_first_enabled_duplicate()
    {
        var first = Skill("same", "/first/SKILL.md", enabled: false);
        var second = Skill("same", "/second/SKILL.md", enabled: true);
        var third = Skill("other", "/third/SKILL.md", enabled: true);
        var mentions = SkillMentionParser.Parse("$HOME then $other, $same, and $same again");

        var selected = SkillSelection.Select([first, second, third], mentions);

        _ = await Assert.That(string.Join(',', mentions.Select(mention => mention.Name)))
            .IsEqualTo("other,same,same");
        _ = await Assert.That(string.Join(',', selected.Select(skill => skill.Path)))
            .IsEqualTo("/third/SKILL.md,/second/SKILL.md");
    }

    [Test]
    [Arguments("$9skill", "9skill")]
    [Arguments("($skill-name).", "skill-name")]
    [Arguments("$plugin:skill", "plugin:skill")]
    [Arguments("email$x", "")]
    [Arguments("$PATH and $skill", "skill")]
    public async Task Mention_syntax_respects_boundaries_and_environment_names(string text, string expected)
    {
        var mentions = SkillMentionParser.Parse(text);

        _ = await Assert.That(string.Join(',', mentions.Select(mention => mention.Name))).IsEqualTo(expected);
    }

    [Test]
    public async Task Linked_mentions_select_the_exact_duplicate_path()
    {
        var first = Skill("same", Path.GetFullPath("/first/SKILL.md"), enabled: true);
        var second = Skill("same", Path.GetFullPath("/second/SKILL.md"), enabled: true);
        var mentions = SkillMentionParser.Parse($"use [$same]({second.Path})");

        var selected = SkillSelection.Select([first, second], mentions);

        _ = await Assert.That(selected).HasSingleItem();
        _ = await Assert.That(selected[0].Path).IsEqualTo(second.Path);
    }

    [Test]
    public async Task Catalog_rendering_is_bounded_and_reports_omissions()
    {
        var skills = Enumerable.Range(0, 500)
            .Select(index => new SkillMetadata(
                $"skill-{index:D3}",
                new string('d', 1024),
                $"/skills/{index:D3}/SKILL.md",
                SkillScope.User,
                null,
                null,
                true,
                true))
            .ToArray();

        var rendered = AgentSkillPromptProvider.Render(skills, TestModels.PromptTemplates);

        _ = await Assert.That(Encoding.UTF8.GetByteCount(rendered)).IsLessThanOrEqualTo(64 * 1024);
        _ = await Assert.That(rendered).Contains("additional skill(s) omitted");
    }

    [Test]
    public async Task Packaged_assets_match_the_pinned_codex_sample_manifest()
    {
        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "skills"));
        var files = RelativeFiles(packaged);

        _ = await Assert.That(files.Count).IsEqualTo(78);
        var manifest = await Manifest(packaged, files);
        _ = await Assert.That(manifest).IsEqualTo("77604C1A24FD3343E1028A3FE4A66C76D7535BFCB34F171A31FF6694AB271472");
    }

    private static SkillMetadata Skill(string name, string path, bool enabled) =>
        new(name, "description", path, SkillScope.User, null, null, enabled, true);

    private static List<string> RelativeFiles(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)];

    private static async Task<string> Manifest(string root, IReadOnlyList<string> relativePaths)
    {
        using var manifest = new MemoryStream();
        foreach (var relativePath in relativePaths)
        {
            await using var stream = File.OpenRead(Path.Combine(root, relativePath));
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            await manifest.WriteAsync(Encoding.UTF8.GetBytes($"{hash}  {relativePath}\n"));
        }

        manifest.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(manifest));
    }
}
