using Parrot.Config;
using Parrot.Security;

namespace Parrot.Core.Tests;

internal sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-config-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task A_missing_user_file_is_layered_over_the_generated_predefined_configuration(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "config.yaml");
        var predefined = Path.Combine(_directory, "predefined_config.yaml");
        var configuration = Load(path);

        _ = await Assert.That(File.Exists(path)).IsFalse();
        _ = await Assert.That(configuration.Model).IsEmpty();
        _ = await Assert.That(configuration.InlineDiff).IsTrue();
        _ = await Assert.That(configuration.WebFetch.AllowPrivate).IsFalse();
        _ = await Assert.That(configuration.ModelAliases).Count().IsEqualTo(4);
        _ = await Assert.That(configuration.Profiles).Count().IsEqualTo(7);
        var build = configuration.Profiles["build"];
        _ = await Assert.That(build.Prompt).IsEqualTo("You are Parrot's build mode. Implement and verify the requested changes.");
        _ = await Assert.That(build.HardRules[0]).IsEqualTo("Keep tool side effects within the authorized workspace.");
        _ = await Assert.That(build.MaxTurns).IsEqualTo(1024);
        _ = await Assert.That(build.ReadOnly).IsFalse();
        _ = await Assert.That(build.SandboxRules).IsEmpty();
        _ = await Assert.That(configuration.Profiles["plan"].MaxTurns).IsEqualTo(1024);
        _ = await Assert.That(configuration.Profiles["query"].ReadOnly).IsTrue();
        _ = await Assert.That(await File.ReadAllTextAsync(predefined, cancellationToken))
            .Contains("Predefined configuration reference.");
    }

    [Test]
    public async Task The_model_is_read_from_the_file()
    {
        var path = Write("# parrot config\nmodel: deepseek-v4-pro\n");

        _ = await Assert.That(Load(path).Model).IsEqualTo("deepseek-v4-pro");
    }

    [Test]
    public async Task Inline_diff_defaults_to_true_and_accepts_boolean_configuration()
    {
        var missing = Load(Path.Combine(_directory, "missing.yaml"));
        var enabled = Load(Write("inline_diff: true\n"));
        var disabled = Load(Write("inline_diff: false\n"));

        _ = await Assert.That(missing.InlineDiff).IsTrue();
        _ = await Assert.That(enabled.InlineDiff).IsTrue();
        _ = await Assert.That(disabled.InlineDiff).IsFalse();
    }

    [Test]
    public async Task Inline_diff_rejects_non_boolean_values()
    {
        var path = Write("inline_diff: yes\n");

        _ = await Assert.That(() => Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Web_fetch_private_access_is_opt_in()
    {
        var missing = Load(Path.Combine(_directory, "missing.yaml"));
        var configured = Load(Write("web_fetch:\n  allow_private: true\n"));

        _ = await Assert.That(missing.WebFetch.AllowPrivate).IsFalse();
        _ = await Assert.That(configured.WebFetch.AllowPrivate).IsTrue();
    }

    [Test]
    public async Task Security_configuration_is_strict_and_ordered()
    {
        var configuration = Load(Write("""
            sandbox_rules:
              - path: /workspace
                rule: allow_write
              - path: /workspace/private
                rule: deny_read
            profiles:
              build:
                read_only: false
                sandbox_rules:
                  - path: /workspace/generated
                    rule: deny_write
              plan:
                sandbox_rules: []
              query:
                read_only: true
            """));

        _ = await Assert.That(configuration.SandboxRules.Count).IsEqualTo(2);
        _ = await Assert.That(configuration.SandboxRules[0])
            .IsEqualTo(new SandboxRule("/workspace", SandboxRuleAction.AllowWrite));
        _ = await Assert.That(configuration.SandboxRules[1].Action).IsEqualTo(SandboxRuleAction.DenyRead);
        _ = await Assert.That(configuration.Profiles["build"].ReadOnly).IsFalse();
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules[0].Action)
            .IsEqualTo(SandboxRuleAction.DenyWrite);
        _ = await Assert.That(configuration.Profiles["plan"].ReadOnly).IsTrue();
        _ = await Assert.That(configuration.Profiles["query"].ReadOnly).IsTrue();
    }

    [Test]
    public async Task Profile_defaults_include_all_policies_and_distinguish_omitted_from_empty_tool_allowlists()
    {
        var configuration = Load(Write(string.Empty));

        _ = await Assert.That(configuration.DefaultProfile).IsEqualTo("build");
        _ = await Assert.That(configuration.Profiles["build"].MaxTurns).IsEqualTo(1024);
        _ = await Assert.That(configuration.Profiles["query"].MaxTurns).IsEqualTo(8);
        _ = await Assert.That(configuration.Profiles["explorer"].MaxTurns).IsEqualTo(32);
        _ = await Assert.That(configuration.Profiles["worker"].MaxTurns).IsEqualTo(128);
        _ = await Assert.That(configuration.Profiles["thinker"].MaxTurns).IsEqualTo(256);
        _ = await Assert.That(configuration.Profiles["worker"].AllowedTools).IsNull();
        _ = await Assert.That(configuration.Profiles["thinker"].AllowedTools?.Count).IsEqualTo(3);

        var noTools = Load(Write("profiles:\n  worker:\n    allowed_tools: []\n"));
        _ = await Assert.That(noTools.Profiles["worker"].AllowedTools).IsEmpty();
    }

    [Test]
    public async Task Profile_fields_partially_override_predefined_definitions()
    {
        var profile = Load(Write("""
            profiles:
              plan:
                prompt: Custom plan guidance
                max_turns: 7
            """)).Profiles["plan"];

        _ = await Assert.That(profile.Prompt).IsEqualTo("Custom plan guidance");
        _ = await Assert.That(profile.MaxTurns).IsEqualTo(7);
        _ = await Assert.That(profile.HardRules[0]).IsEqualTo(
            "Read-only mode is enforced by the runtime except for the designated plan-artifact directory. Writes outside that directory are prohibited.");
        _ = await Assert.That(profile.ReadOnly).IsTrue();
        _ = await Assert.That(profile.SandboxRules).IsEmpty();
    }

    [Test]
    public async Task Invalid_security_configuration_fails_closed()
    {
        var relativePath = Write("sandbox_rules:\n  - path: relative\n    rule: allow_write\n");
        _ = await Assert.That(() => Load(relativePath)).Throws<InvalidDataException>();

        var invalidAction = Write("sandbox_rules:\n  - path: /workspace\n    rule: unknown\n");
        _ = await Assert.That(() => Load(invalidAction)).Throws<InvalidDataException>();

        var partialProfile = Write("profiles:\n  worker:\n    read_only: true\n");
        _ = await Assert.That(Load(partialProfile).Profiles["worker"].ReadOnly).IsTrue();

        var invalidBoolean = Write("profiles:\n  query:\n    read_only: yes\n");
        _ = await Assert.That(() => Load(invalidBoolean)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Retired_profile_status_is_ignored_at_the_configuration_boundary()
    {
        var configuration = Load(Write("profiles:\n  query:\n    status: retired\n"));

        _ = await Assert.That(configuration.Profiles["query"].Prompt).Contains("query mode");
    }

    [Test]
    [Arguments("profiles:\n  query:\n    read_ony: true\n")]
    [Arguments("sandbox_rules:\n  - path: /workspace\n    rules: allow_write\n")]
    public async Task Security_configuration_rejects_unknown_keys(string content)
    {
        var path = Write(content);

        _ = await Assert.That(() => Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("profiles:\n  build:\n    prompt: ''\n")]
    [Arguments("profiles:\n  build:\n    hard_rules: []\n")]
    [Arguments("profiles:\n  build:\n    max_turns: 0\n")]
    [Arguments("profiles:\n  build:\n    max_turns: -1\n")]
    [Arguments("profiles:\n  build:\n    max_turns: not-a-number\n")]
    [Arguments("profiles:\n  build:\n    max_turns: 1.5\n")]
    public async Task Profile_configuration_rejects_invalid_required_fields(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    public async Task Model_aliases_have_four_exact_defaults()
    {
        var aliases = Load(Path.Combine(_directory, "config.yaml")).ModelAliases;

        _ = await Assert.That(aliases).Count().IsEqualTo(4);
        _ = await Assert.That(aliases["low_llm"]).IsEqualTo(new ModelAliasConfig(
            string.Empty,
            "mechanical, single file task, text or code processing when no suitable cli tool available.",
            null));
        _ = await Assert.That(aliases["medium_llm"]).IsEqualTo(new ModelAliasConfig(
            string.Empty,
            "Decently capable, specific clear task, component level task, two or three file window",
            null));
        _ = await Assert.That(aliases["high_llm"]).IsEqualTo(new ModelAliasConfig(
            string.Empty,
            "General purpose, agent spawner, tactical decision making and planning, debugging, colaborator",
            null));
        _ = await Assert.That(aliases["xhigh_llm"]).IsEqualTo(new ModelAliasConfig(
            string.Empty,
            "Strategic work spanning multiple modules or parties, ambiguous or open-ended requirements, hard debugging or optimization, and high-level planning where cheaper models are insufficient.",
            "For complex work, delegate focused exploration or implementation when the active profile permits it. Do not duplicate a task already handled by a running agent."));
    }

    [Test]
    public async Task Model_aliases_partially_override_defaults_and_add_custom_aliases()
    {
        var aliases = Load(Write("""
            model_aliases:
              low_llm:
                model_string: openai/gpt-5
              xhigh_llm:
                usage: Specialized strategic work
              local:
                model_string: ollama/qwen3
                usage: Local implementation work
                augment_system_prompt: Use local tools first.
            """)).ModelAliases;

        _ = await Assert.That(aliases["low_llm"]).IsEqualTo(new ModelAliasConfig(
            "openai/gpt-5",
            "mechanical, single file task, text or code processing when no suitable cli tool available.",
            null));
        _ = await Assert.That(aliases["xhigh_llm"]).IsEqualTo(new ModelAliasConfig(
            string.Empty,
            "Specialized strategic work",
            "For complex work, delegate focused exploration or implementation when the active profile permits it. Do not duplicate a task already handled by a running agent."));
        _ = await Assert.That(aliases["local"]).IsEqualTo(new ModelAliasConfig(
            "ollama/qwen3", "Local implementation work", "Use local tools first."));
    }

    [Test]
    public async Task Model_alias_augmentation_distinguishes_null_from_an_explicit_empty_string()
    {
        var aliases = Load(Write("""
            model_aliases:
              custom:
                usage: Custom work
              low_llm:
                augment_system_prompt: ""
            """)).ModelAliases;

        _ = await Assert.That(aliases["custom"].AugmentSystemPrompt).IsNull();
        _ = await Assert.That(aliases["low_llm"].AugmentSystemPrompt).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Model_augment_system_prompts_are_a_canonical_selector_map()
    {
        var prompts = Load(Write("""
            model_augment_system_prompts:
              openai/gpt-5: First prompt
              anthropic/claude-sonnet: Second prompt
            """)).ModelAugmentSystemPrompts;

        _ = await Assert.That(prompts).Count().IsEqualTo(2);
        _ = await Assert.That(prompts["openai/gpt-5"]).IsEqualTo("First prompt");
        _ = await Assert.That(prompts["anthropic/claude-sonnet"]).IsEqualTo("Second prompt");
    }

    [Test]
    [Arguments("model_aliases:\n  '':\n    usage: Empty name\n")]
    [Arguments("model_aliases:\n  ' spaced ':\n    usage: Spaced name\n")]
    [Arguments("model_aliases:\n  provider/name:\n    usage: Slash name\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: ''\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: [not, a, string]\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    model_string: no-slash\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    model_string: ' provider/model'\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    model_string: provider//model\n")]
    [Arguments("model_aliases: []\n")]
    [Arguments("model_aliases:\n  custom: A usage\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    unsupported: value\n")]
    [Arguments("model_augment_system_prompts: []\n")]
    [Arguments("model_augment_system_prompts:\n  provider/model: [not, a, prompt]\n")]
    [Arguments("model_augment_system_prompts:\n  no-slash: prompt\n")]
    public async Task Invalid_model_alias_configuration_is_rejected(string content)
    {
        var path = Write(content);

        _ = await Assert.That(() => Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Set_model_alias_persists_reload_and_unrelated_yaml()
    {
        var path = Write("""
            theme: dark
            model_aliases:
              low_llm:
                usage: Fast local work
            """);

        var configuration = Load(path);
        configuration.SetModelAlias("low_llm", "openai/gpt-5.6");

        var reloaded = Load(path);
        var rewritten = await File.ReadAllTextAsync(path);
        _ = await Assert.That(reloaded.ModelAliases["low_llm"]).IsEqualTo(new ModelAliasConfig(
            "openai/gpt-5.6", "Fast local work", null));
        _ = await Assert.That(rewritten).Contains("theme: dark");
    }

    [Test]
    public async Task User_configuration_recursively_overrides_predefined_mappings(CancellationToken cancellationToken)
    {
        var path = Write("""
            model_aliases:
              low_llm:
                model_string: openai/gpt-5
            profiles:
              build:
                sandbox_rules:
                  - path: /workspace
                    rule: allow_write
            """);
        var configuration = Load(path);

        _ = await Assert.That(configuration.ModelAliases["low_llm"]).IsEqualTo(new ModelAliasConfig(
            "openai/gpt-5",
            "mechanical, single file task, text or code processing when no suitable cli tool available.",
            null));
        _ = await Assert.That(configuration.Profiles["build"].ReadOnly).IsFalse();
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules.Count).IsEqualTo(1);
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules[0])
            .IsEqualTo(new SandboxRule("/workspace", SandboxRuleAction.AllowWrite));
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).Contains("model_string: openai/gpt-5");
    }

    [Test]
    public async Task Loading_replaces_the_predefined_reference_without_rewriting_the_user_configuration(
        CancellationToken cancellationToken)
    {
        var path = Write("model: openai/gpt-5\n");
        var predefined = Path.Combine(_directory, "predefined_config.yaml");
        _ = Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(predefined, "model: stale/model\n", cancellationToken);

        _ = Load(path);

        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("model: openai/gpt-5\n");
        _ = await Assert.That(await File.ReadAllTextAsync(predefined, cancellationToken))
            .Contains("Predefined configuration reference.");
    }

    [Test]
    public async Task Set_model_alias_rejects_an_undefined_alias()
    {
        var configuration = Load(Path.Combine(_directory, "config.yaml"));

        _ = await Assert.That(() => configuration.SetModelAlias("undefined", "openai/gpt-5.6"))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Set_model_persists_and_survives_a_reload()
    {
        var path = Path.Combine(_directory, "config.yaml");

        Load(path).SetModel("glm-5.2");

        _ = await Assert.That(Load(path).Model).IsEqualTo("glm-5.2");
    }

    [Test]
    public async Task Set_model_replaces_only_that_key_and_keeps_the_others(CancellationToken cancellationToken)
    {
        var path = Write("model: old\ntheme: dark\n");

        Load(path).SetModel("new");

        var rewritten = await File.ReadAllTextAsync(path, cancellationToken);

        _ = await Assert.That(rewritten).Contains("model: new");
        _ = await Assert.That(rewritten).Contains("theme: dark");
        _ = await Assert.That(rewritten).DoesNotContain("old");
    }

    [Test]
    public async Task Set_model_adds_the_key_when_absent(CancellationToken cancellationToken)
    {
        var path = Write("theme: dark\n");

        Load(path).SetModel("added");

        _ = await Assert.That(Load(path).Model).IsEqualTo("added");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).Contains("theme: dark");
    }

    private Configuration Load(string path) =>
        Configuration.Load(path, Path.Combine(_directory, "predefined_config.yaml"));

    private string Write(string content)
    {
        _ = Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
