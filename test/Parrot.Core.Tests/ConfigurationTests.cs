using System.Collections.ObjectModel;
using System.Text.Json;
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
    [Arguments("read_only_exec_command_prefixes: null", "read_only_exec_command_prefixes must be a string sequence")]
    [Arguments("read_only_exec_command_prefixes:\n  - ''", "read_only_exec_command_prefixes[19] must be a unique nonblank string without leading or trailing whitespace")]
    [Arguments("read_only_exec_command_prefixes:\n  - '  rg'", "read_only_exec_command_prefixes[19] must be a unique nonblank string without leading or trailing whitespace")]
    [Arguments("read_only_exec_command_prefixes:\n  - rg\n  - rg", "read_only_exec_command_prefixes[19] must be a unique nonblank string without leading or trailing whitespace")]
    [Arguments("read_only_exec_command_prefixes:\n  - [rg]", "read_only_exec_command_prefixes[19] must be a unique nonblank string without leading or trailing whitespace")]
    public async Task Invalid_read_only_exec_command_prefixes_are_rejected(string yaml, string message)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Load(Write(yaml + "\n")));

        _ = await Assert.That(exception.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Read_only_exec_command_prefixes_are_exposed_as_read_only_collection()
    {
        var prefixes = Load(Path.Combine(_directory, "defaults.yaml")).ReadOnlyExecCommandPrefixes;

        _ = await Assert.That(prefixes).IsNotNull();
        _ = await Assert.That(prefixes).IsTypeOf<ReadOnlyCollection<string>>();
    }

    [Test]
    public async Task Provider_model_defaults_are_separate_from_declared_models()
    {
        var configuration = Load(Write("""
            providers:
              custom:
                model_defaults:
                  seeded:
                    name: Seeded
                    context: 128
                    max_tokens: 32
                    input_price: 0.000001
                    cached_input_price: 0.0000001
                    output_price: 0.000002
                    tools: true
                    reasoning: false
                    output: [text]
                models:
                  declared:
                    name: Declared
                    context: 256
                    max_tokens: 64
                    tools: false
                    reasoning: true
                    output: [text]
            """));

        var provider = configuration.Providers["custom"];
        _ = await Assert.That(provider.ModelDefaults.Keys).Contains("seeded");
        _ = await Assert.That(provider.ModelDefaults["seeded"].InputPrice).IsEqualTo(0.000001);
        _ = await Assert.That(provider.ModelDefaults["seeded"].CachedInputPrice).IsEqualTo(0.0000001);
        _ = await Assert.That(provider.ModelDefaults["seeded"].OutputPrice).IsEqualTo(0.000002);
        _ = await Assert.That(provider.ModelDefaults.Keys).DoesNotContain("declared");
        _ = await Assert.That(provider.Models.Keys).Contains("declared");
        _ = await Assert.That(provider.Models.Keys).DoesNotContain("seeded");
    }

    [Test]
    public async Task Openrouter_provider_preferences_partially_override_predefined_privacy_defaults(
        CancellationToken cancellationToken)
    {
        const string userConfiguration = """
            providers:
              openrouter:
                provider_preferences:
                  allow_fallbacks: false
            """;
        var path = Write(userConfiguration);

        var configuration = Load(path);

        using var preferences = JsonDocument.Parse(configuration.Providers["openrouter"].ProviderPreferences);
        var providerPreferences = preferences.RootElement;
        _ = await Assert.That(providerPreferences.EnumerateObject().Count()).IsEqualTo(4);
        _ = await Assert.That(providerPreferences.GetProperty("allow_fallbacks").GetBoolean()).IsFalse();
        _ = await Assert.That(providerPreferences.GetProperty("require_parameters").GetBoolean()).IsTrue();
        _ = await Assert.That(providerPreferences.GetProperty("data_collection").GetString()).IsEqualTo("deny");
        _ = await Assert.That(providerPreferences.GetProperty("zdr").GetBoolean()).IsTrue();
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo(userConfiguration);
    }

    [Test]
    [Arguments("-0.1")]
    [Arguments("NaN")]
    [Arguments("Infinity")]
    [Arguments("not-a-number")]
    public async Task Provider_model_prices_must_be_finite_non_negative_numbers(string price)
    {
        var path = Write($"providers:\n  custom:\n    models:\n      m:\n        cached_input_price: {price}\n");

        _ = await Assert.That(() => Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task The_model_is_read_from_the_file()
    {
        var path = Write("# parrot config\nmodel: deepseek-v4-pro\n");

        _ = await Assert.That(Load(path).Model).IsEqualTo("deepseek-v4-pro");
    }

    [Test]
    public async Task System_prompts_override_inherited_entries_and_add_namespaced_providers()
    {
        var prompts = Load(Write("""
            system_prompts:
              runtime:system-context:01-base: Custom base prompt.
              custom:guidance: Additional guidance.
            """)).SystemPrompts;

        _ = await Assert.That(prompts).Count().IsEqualTo(4);
        _ = await Assert.That(prompts["runtime:system-context:01-base"]).IsEqualTo("Custom base prompt.");
        _ = await Assert.That(prompts["runtime:system-context:02-delegation"])
            .StartsWith("# Agent delegation\nPrefer to split larger task to subagent with a well defined scope.");
        _ = await Assert.That(prompts["runtime:system-context:03-subagent-pattern"])
            .StartsWith("# Common subagent spawn strategy");
        _ = await Assert.That(prompts["custom:guidance"]).IsEqualTo("Additional guidance.");
    }

    [Test]
    public async Task Legacy_top_level_prompt_is_not_used()
    {
        var configuration = Load(Write("prompt: Legacy prompt.\n"));

        _ = await Assert.That(configuration.SystemPrompts["runtime:system-context:01-base"])
            .DoesNotContain("Legacy prompt.");
    }

    [Test]
    [Arguments("system_prompts: []\n")]
    [Arguments("system_prompts:\n  custom:guidance: ''\n")]
    [Arguments("system_prompts:\n  custom:guidance: '   '\n")]
    [Arguments("system_prompts:\n  custom:guidance: null\n")]
    [Arguments("system_prompts:\n  custom:guidance: ~\n")]
    [Arguments("system_prompts:\n  custom:guidance: []\n")]
    [Arguments("system_prompts:\n  guidance: value\n")]
    [Arguments("system_prompts:\n  ':guidance': value\n")]
    [Arguments("system_prompts:\n  'custom:': value\n")]
    [Arguments("system_prompts:\n  ' custom:guidance': value\n")]
    [Arguments("system_prompts:\n  'custom:guidance ': value\n")]
    [Arguments("system_prompts:\n  \"custom:guidance\\tbad\": value\n")]
    public async Task System_prompts_require_namespaced_keys_and_non_empty_string_values(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

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
    [Arguments(1)]
    [Arguments(25)]
    public async Task Live_buffer_rows_accepts_positive_user_configuration(int rows)
    {
        var configuration = Load(Write($"live_buffer_rows: {rows}\n"));

        _ = await Assert.That(configuration.LiveBufferRows).IsEqualTo(rows);
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("1.5")]
    [Arguments("true")]
    [Arguments("null")]
    [Arguments("words")]
    [Arguments("2147483648")]
    [Arguments("[]")]
    public async Task Live_buffer_rows_requires_a_positive_integer(string value)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Load(Write($"live_buffer_rows: {value}\n")));

        _ = await Assert.That(exception.Message).IsEqualTo("live_buffer_rows must be a positive integer");
    }

    [Test]
    public async Task User_input_timeout_defaults_to_twenty_minutes_and_accepts_positive_milliseconds()
    {
        var missing = Load(Path.Combine(_directory, "missing.yaml"));
        var configured = Load(Write("user_input_timeout_ms: 1250\n"));

        _ = await Assert.That(missing.UserInputTimeout).IsEqualTo(TimeSpan.FromMinutes(20));
        _ = await Assert.That(configured.UserInputTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1250));
    }

    [Test]
    public async Task User_input_timeout_accepts_the_infinite_sentinel()
    {
        var configuration = Load(Write("user_input_timeout_ms: -1\n"));

        _ = await Assert.That(configuration.UserInputTimeout).IsEqualTo(Timeout.InfiniteTimeSpan);
    }

    [Test]
    public async Task Deprecated_permission_request_timeout_is_a_user_configuration_fallback()
    {
        var fallback = Load(Write("permission_request_timeout_ms: 2500\n"));
        var canonical = Load(Write("permission_request_timeout_ms: 2500\nuser_input_timeout_ms: 1250\n"));

        _ = await Assert.That(fallback.UserInputTimeout).IsEqualTo(TimeSpan.FromMilliseconds(2500));
        _ = await Assert.That(canonical.UserInputTimeout).IsEqualTo(TimeSpan.FromMilliseconds(1250));
    }

    [Test]
    [Arguments("user_input_timeout_ms: 0\n")]
    [Arguments("user_input_timeout_ms: -2\n")]
    [Arguments("user_input_timeout_ms: 1.5\n")]
    [Arguments("user_input_timeout_ms: true\n")]
    [Arguments("user_input_timeout_ms: null\n")]
    [Arguments("user_input_timeout_ms: 2147483648\n")]
    [Arguments("permission_request_timeout_ms: 0\n")]
    public async Task User_input_timeout_requires_positive_integer_milliseconds_or_the_infinite_sentinel(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    [Arguments("2", 2)]
    [Arguments("2147483647", int.MaxValue)]
    public async Task Agent_task_configuration_overrides_maximum_attempts(string value, int expected)
    {
        var configuration = Load(Write($"agent_tasks:\n  maximum_attempts: {value}\n"));

        _ = await Assert.That(configuration.AgentTasks.MaximumAttempts).IsEqualTo(expected);
    }

    [Test]
    [Arguments("agent_tasks: null\n")]
    [Arguments("agent_tasks: 5\n")]
    [Arguments("agent_tasks: []\n")]
    public async Task Agent_task_configuration_must_be_a_mapping(string content) =>
        _ = await Assert.That(() => Load(Write(content)))
            .Throws<InvalidDataException>().WithMessage("agent_tasks must be a mapping");

    [Test]
    public async Task Agent_task_configuration_rejects_unknown_keys() =>
        _ = await Assert.That(() => Load(Write("agent_tasks:\n  unsupported: 1\n")))
            .Throws<InvalidDataException>().WithMessage("agent_tasks contains an unsupported key");

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("1.5")]
    [Arguments("true")]
    [Arguments("null")]
    [Arguments("words")]
    [Arguments("2147483648")]
    public async Task Agent_task_configuration_requires_a_positive_integer_maximum_attempts(string value) =>
        _ = await Assert.That(() => Load(Write($"agent_tasks:\n  maximum_attempts: {value}\n")))
            .Throws<InvalidDataException>().WithMessage("agent_tasks.maximum_attempts must be a positive integer");

    [Test]
    public async Task Compaction_fields_partially_override_predefined_definitions()
    {
        var trigger = Load(Write("compaction:\n  trigger_percent: 80\n")).Compaction;
        var target = Load(Write("compaction:\n  target_percent: 20\n")).Compaction;
        var maximum = Load(Write("compaction:\n  maximum_input_tokens: 2048\n")).Compaction;
        var summary = Load(Write("compaction:\n  summary_output_tokens: 512\n")).Compaction;

        _ = await Assert.That(trigger.TriggerPercent).IsEqualTo(80);
        _ = await Assert.That(trigger.TargetPercent).IsEqualTo(30);
        _ = await Assert.That(target.TriggerPercent).IsEqualTo(95);
        _ = await Assert.That(target.TargetPercent).IsEqualTo(20);
        _ = await Assert.That(maximum.MaximumInputTokens).IsEqualTo(2_048);
        _ = await Assert.That(maximum.SummaryOutputTokens).IsEqualTo(12_000);
        _ = await Assert.That(summary.MaximumInputTokens).IsEqualTo(60_000);
        _ = await Assert.That(summary.SummaryOutputTokens).IsEqualTo(512);
    }

    [Test]
    [Arguments("compaction: null\n")]
    [Arguments("compaction: 60000\n")]
    [Arguments("compaction: []\n")]
    public async Task Compaction_configuration_must_be_a_mapping(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    [Arguments("compaction:\n  maximum_input_tokens: 60000\n  unsupported: 1\n")]
    [Arguments("compaction:\n  summary_output_tokens: 12000\n  maximum_tokens: 1\n")]
    [Arguments("compaction:\n  trigger_percent: 90\n  target_tokens: 30\n")]
    public async Task Compaction_configuration_rejects_unknown_keys(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    [Arguments("trigger_percent", "0")]
    [Arguments("trigger_percent", "1.5")]
    [Arguments("trigger_percent", "true")]
    [Arguments("trigger_percent", "null")]
    [Arguments("target_percent", "0")]
    [Arguments("target_percent", "1.5")]
    [Arguments("target_percent", "true")]
    [Arguments("target_percent", "null")]
    [Arguments("maximum_input_tokens", "0")]
    [Arguments("maximum_input_tokens", "-1")]
    [Arguments("maximum_input_tokens", "1.5")]
    [Arguments("maximum_input_tokens", "true")]
    [Arguments("maximum_input_tokens", "null")]
    [Arguments("maximum_input_tokens", "2147483648")]
    [Arguments("summary_output_tokens", "0")]
    [Arguments("summary_output_tokens", "-1")]
    [Arguments("summary_output_tokens", "1.5")]
    [Arguments("summary_output_tokens", "true")]
    [Arguments("summary_output_tokens", "null")]
    [Arguments("summary_output_tokens", "2147483648")]
    public async Task Compaction_configuration_requires_positive_integer_fields(string key, string value) =>
        _ = await Assert.That(() => Load(Write($"compaction:\n  {key}: {value}\n")))
            .Throws<InvalidDataException>();

    [Test]
    [Arguments("compaction:\n  trigger_percent: 100\n")]
    [Arguments("compaction:\n  trigger_percent: 30\n  target_percent: 30\n")]
    [Arguments("compaction:\n  trigger_percent: 30\n  target_percent: 31\n")]
    public async Task Compaction_percentages_require_target_to_be_less_than_trigger_between_one_and_ninety_nine(
        string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    public async Task Cli_utility_sequences_append_independently_and_expected_wins_across_lists()
    {
        var defaults = Load(Path.Combine(_directory, "missing.yaml")).CliUtilities;
        var candidates = Load(Write("""
            cli_utilities:
              expected:
                - custom
                - shared
              optional:
                - other
                - shared
            """)).CliUtilities;

        _ = await Assert.That(candidates.Expected.SequenceEqual(
            defaults.Expected.Concat(["custom", "shared"]),
            StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(candidates.Optional.SequenceEqual(
            defaults.Optional.Concat(["other"]),
            StringComparer.Ordinal)).IsTrue();

        var expectedOnly = Load(Write("cli_utilities:\n  expected: [docker]\n")).CliUtilities;
        _ = await Assert.That(expectedOnly.Expected.SequenceEqual(
            defaults.Expected.Concat(["docker"]),
            StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(expectedOnly.Optional).Count().IsEqualTo(defaults.Optional.Count - 1);
        _ = await Assert.That(expectedOnly.Optional).DoesNotContain("docker");
    }

    [Test]
    public async Task Cli_utility_sequences_can_explicitly_replace_defaults()
    {
        var candidates = Load(Write("""
            cli_utilities:
              expected: !replace [custom]
              optional: !replace []
            """)).CliUtilities;

        _ = await Assert.That(candidates.Expected.SequenceEqual(["custom"], StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(candidates.Optional).IsEmpty();
    }

    [Test]
    [Arguments("cli_utilities: []\n")]
    [Arguments("cli_utilities:\n  expected: null\n")]
    [Arguments("cli_utilities:\n  optional: value\n")]
    [Arguments("cli_utilities:\n  expected: [git]\n")]
    [Arguments("cli_utilities:\n  expected: [git, git]\n")]
    [Arguments("cli_utilities:\n  optional: [git, git]\n")]
    [Arguments("cli_utilities:\n  expected: ['']\n")]
    [Arguments("cli_utilities:\n  expected: ['git hub']\n")]
    [Arguments("cli_utilities:\n  expected: ['git\\hub']\n")]
    [Arguments("cli_utilities:\n  expected: ['bin/git']\n")]
    [Arguments("cli_utilities:\n  extra: []\n")]
    public async Task Invalid_cli_utility_configuration_is_rejected(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    public async Task Web_fetch_private_access_is_opt_in()
    {
        var missing = Load(Path.Combine(_directory, "missing.yaml"));
        var configured = Load(Write("web_fetch:\n  allow_private: true\n"));

        _ = await Assert.That(missing.WebFetch.AllowPrivate).IsFalse();
        _ = await Assert.That(configured.WebFetch.AllowPrivate).IsTrue();
    }

    [Test]
    public async Task Disabled_tools_contains_only_effective_true_entries()
    {
        var configuration = Load(Write("""
            disabled_tools:
              web_fetch: true
              agent_spawn: false
              future_tool: true
            """));

        _ = await Assert.That(configuration.DisabledTools).Count().IsEqualTo(2);
        _ = await Assert.That(configuration.DisabledTools).Contains("web_fetch");
        _ = await Assert.That(configuration.DisabledTools).Contains("future_tool");
        _ = await Assert.That(configuration.DisabledTools).DoesNotContain("agent_spawn");
    }

    [Test]
    [Arguments("disabled_tools: null\n")]
    [Arguments("disabled_tools: web_fetch\n")]
    [Arguments("disabled_tools: []\n")]
    [Arguments("disabled_tools:\n  web_fetch: yes\n")]
    [Arguments("disabled_tools:\n  web_fetch: null\n")]
    [Arguments("disabled_tools:\n  web_fetch: {}\n")]
    [Arguments("disabled_tools:\n  web_fetch: []\n")]
    [Arguments("disabled_tools:\n  '': true\n")]
    [Arguments("disabled_tools:\n  ' web_fetch': true\n")]
    [Arguments("disabled_tools:\n  'web_fetch ': true\n")]
    public async Task Disabled_tools_rejects_invalid_configuration(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    public async Task Security_configuration_is_strict_and_ordered()
    {
        var workspace = Path.Combine(_directory, "workspace");
        var privateWorkspace = Path.Combine(workspace, "private");
        _ = Directory.CreateDirectory(workspace);
        _ = Directory.CreateDirectory(privateWorkspace);
        var configuration = Load(Write($"""
            sandbox_rules:
              - path: {workspace}
                rule: allow_write
              - path: {privateWorkspace}
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

        _ = await Assert.That(configuration.SandboxRules.Count).IsEqualTo(7);
        _ = await Assert.That(configuration.SandboxRules[0].Path).IsEqualTo("/tmp");
        _ = await Assert.That(configuration.SandboxRules[^2])
            .IsEqualTo(new SandboxRule(workspace, SandboxRuleAction.AllowWrite));
        _ = await Assert.That(configuration.SandboxRules[^1].Action).IsEqualTo(SandboxRuleAction.DenyRead);
        _ = await Assert.That(configuration.Profiles["build"].ReadOnly).IsFalse();
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules[0].Action)
            .IsEqualTo(SandboxRuleAction.DenyWrite);
        _ = await Assert.That(configuration.Profiles["plan"].ReadOnly).IsTrue();
        _ = await Assert.That(configuration.Profiles["query"].ReadOnly).IsTrue();
    }

    [Test]
    public async Task Sandbox_rule_paths_expand_environment_templates_and_nested_fallbacks()
    {
        var home = Path.Combine(_directory, "home");
        var cache = Path.Combine(_directory, "cache");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["CACHE"] = cache,
            ["ROOT"] = Path.Combine(_directory, "root"),
        };
        var path = Write("""
            sandbox_rules:
              - path: '${ROOT}/first/${CACHE_NAME:-cache}'
                rule: allow_write
                create_if_not_exist: true
              - path: '${CACHE:-${MISSING}/unused}'
                rule: deny_write
            profiles:
              build:
                sandbox_rules:
                  - path: '${ROOT}/profile'
                    rule: deny_read
            """);

        var configuration = Load(path, environment);

        _ = await Assert.That(configuration.SandboxRules.Count).IsEqualTo(7);
        _ = await Assert.That(configuration.SandboxRules[^2]).IsEqualTo(
            new SandboxRule(Path.Combine(environment["ROOT"], "first", "cache"), SandboxRuleAction.AllowWrite));
        _ = await Assert.That(configuration.SandboxRules[^1]).IsEqualTo(
            new SandboxRule(cache, SandboxRuleAction.DenyWrite));
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules)
            .Contains(new SandboxRule(Path.Combine(environment["ROOT"], "profile"), SandboxRuleAction.DenyRead));
    }

    [Test]
    public async Task Predefined_configuration_provisions_shared_write_directories()
    {
        var home = Path.Combine(_directory, "home");
        var cache = Path.Combine(_directory, "cache");
        var configuration = Load(
            Path.Combine(_directory, "config.yaml"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["XDG_CACHE_HOME"] = cache,
            });

        var expected = new[]
        {
            "/tmp",
            cache,
            Path.Combine(home, ".nuget", "packages"),
            Path.Combine(home, ".npm"),
            Path.Combine(home, ".local", "share", "pnpm", "store"),
        };
        _ = await Assert.That(configuration.SandboxRules.Select(rule => rule.Path).SequenceEqual(
            expected,
            StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(configuration.SandboxRules.All(rule => rule.Action == SandboxRuleAction.AllowWrite))
            .IsTrue();
        _ = await Assert.That(expected.Skip(1).All(Directory.Exists)).IsTrue();
    }

    [Test]
    public async Task Predefined_configuration_uses_the_home_cache_fallback_when_xdg_cache_is_missing()
    {
        var home = Path.Combine(_directory, "home");
        var configuration = Load(
            Path.Combine(_directory, "config.yaml"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = home });

        _ = await Assert.That(configuration.SandboxRules.Select(rule => rule.Path))
            .Contains(Path.Combine(home, ".cache"));
        _ = await Assert.That(Directory.Exists(Path.Combine(home, ".cache"))).IsTrue();
    }

    [Test]
    public async Task Empty_user_sandbox_rules_retain_predefined_rules()
    {
        var configuration = Load(
            Write("sandbox_rules: []\n"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Path.Combine(_directory, "home"),
                ["XDG_CACHE_HOME"] = Path.Combine(_directory, "cache"),
            });

        _ = await Assert.That(configuration.SandboxRules).Count().IsEqualTo(5);
        _ = await Assert.That(configuration.SandboxRules[0].Path).IsEqualTo("/tmp");
    }

    [Test]
    public async Task Sandbox_rules_can_explicitly_replace_predefined_rules()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "replacement")).FullName;
        var configuration = Load(Write($"""
            sandbox_rules: !replace
              - path: {root}
                rule: deny_write
            """));

        _ = await Assert.That(configuration.SandboxRules).Count().IsEqualTo(1);
        _ = await Assert.That(configuration.SandboxRules[0])
            .IsEqualTo(new SandboxRule(root, SandboxRuleAction.DenyWrite));

        var empty = Load(Write("sandbox_rules: !replace []\n"));
        _ = await Assert.That(empty.SandboxRules).IsEmpty();
    }

    [Test]
    public async Task Missing_allow_write_rules_are_omitted_unless_creation_is_requested()
    {
        var missing = Path.Combine(_directory, "missing", "target");
        var configuration = Load(Write($"""
            sandbox_rules:
              - path: {missing}
                rule: allow_write
            """));

        _ = await Assert.That(configuration.SandboxRules).Count().IsEqualTo(5);
        _ = await Assert.That(configuration.SandboxRules).DoesNotContain(
            new SandboxRule(missing, SandboxRuleAction.AllowWrite));
        _ = await Assert.That(Directory.Exists(missing)).IsFalse();
    }

    [Test]
    public async Task Requested_allow_write_rule_creates_missing_directories_recursively()
    {
        var missing = Path.Combine(_directory, "missing", "target");
        var configuration = Load(Write($"""
            sandbox_rules:
              - path: {missing}
                rule: allow_write
                create_if_not_exist: true
            """));

        _ = await Assert.That(configuration.SandboxRules).Contains(
            new SandboxRule(missing, SandboxRuleAction.AllowWrite));
        _ = await Assert.That(Directory.Exists(missing)).IsTrue();
    }

    [Test]
    public async Task Missing_non_write_rules_are_retained()
    {
        var missing = Path.Combine(_directory, "missing", "target");
        var configuration = Load(Write($"""
            sandbox_rules:
              - path: {missing}
                rule: deny_read
            """));

        _ = await Assert.That(configuration.SandboxRules).Contains(
            new SandboxRule(missing, SandboxRuleAction.DenyRead));
    }

    [Test]
    [Arguments("allow_read")]
    [Arguments("deny_read")]
    [Arguments("deny_write")]
    public async Task Sandbox_rule_creation_is_allowed_only_for_allow_write(string action)
    {
        var path = Path.Combine(_directory, "missing");

        _ = await Assert.That(() => Load(Write($"""
            sandbox_rules:
              - path: {path}
                rule: {action}
                create_if_not_exist: true
            """))).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("yes")]
    [Arguments("null")]
    [Arguments("[]")]
    public async Task Sandbox_rule_creation_requires_a_boolean(string value)
    {
        _ = await Assert.That(() => Load(Write($"""
            sandbox_rules:
              - path: /tmp
                rule: allow_write
                create_if_not_exist: {value}
            """))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Sandbox_rule_creation_rejects_a_path_that_is_an_existing_file(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "file");
        await File.WriteAllTextAsync(file, string.Empty, cancellationToken);
        var impossibleDirectory = Path.Combine(file, "child");

        _ = await Assert.That(() => Load(Write($"""
            sandbox_rules:
              - path: {impossibleDirectory}
                rule: allow_write
                create_if_not_exist: true
            """))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Rejected_configuration_does_not_create_requested_sandbox_directories()
    {
        var directory = Path.Combine(_directory, "missing", "target");

        _ = await Assert.That(() => Load(Write($"""
            sandbox_rules:
              - path: {directory}
                rule: allow_write
                create_if_not_exist: true
            profiles:
              query:
                read_only: invalid
            """))).Throws<InvalidDataException>();
        _ = await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    [Test]
    public async Task Invalid_created_directory_path_is_reported_as_invalid_configuration()
    {
        var invalid = string.Concat(Path.DirectorySeparatorChar, new string('x', 5000));
        var path = Write($"""
            sandbox_rules:
              - path: '{invalid}'
                rule: allow_write
                create_if_not_exist: true
            """);

        _ = await Assert.That(() => Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("${}")]
    [Arguments("${1ROOT}")]
    [Arguments("${ROOT-default}")]
    [Arguments("${ROOT")]
    [Arguments("/root}")]
    public async Task Sandbox_rule_paths_reject_malformed_environment_templates(string template)
    {
        var path = Write($"sandbox_rules:\n  - path: '{template}'\n    rule: allow_write\n");

        _ = await Assert.That(() => Load(path, new Dictionary<string, string>(StringComparer.Ordinal)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Sandbox_rule_paths_reject_missing_empty_and_relative_environment_values()
    {
        var required = Write("sandbox_rules:\n  - path: '${ROOT}'\n    rule: allow_write\n");
        _ = await Assert.That(() => Load(required, new Dictionary<string, string>(StringComparer.Ordinal)))
            .Throws<InvalidDataException>();
        _ = await Assert.That(() => Load(
                required,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["ROOT"] = string.Empty }))
            .Throws<InvalidDataException>();
        _ = await Assert.That(() => Load(
                required,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["ROOT"] = "relative" }))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Environment_values_are_not_recursively_expanded()
    {
        var root = Path.Combine(_directory, "${OTHER}");
        var configuration = Load(
            Write("sandbox_rules:\n  - path: '${ROOT}'\n    rule: allow_write\n    create_if_not_exist: true\n"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = Path.Combine(_directory, "home"),
                ["XDG_CACHE_HOME"] = Path.Combine(_directory, "cache"),
                ["ROOT"] = root,
                ["OTHER"] = "expanded",
            });

        _ = await Assert.That(configuration.SandboxRules[^1].Path).IsEqualTo(root);
    }

    [Test]
    public async Task Non_selected_sequences_continue_to_replace_defaults()
    {
        var configuration = Load(Write("""
            profiles:
              thinker:
                allowed_tools: [read]
            providers:
              chatgpt:
                model_defaults:
                  gpt-5.6-sol:
                    output: [custom]
            """));

        _ = await Assert.That(configuration.Profiles["thinker"].AllowedTools?.SequenceEqual(
            ["read"],
            StringComparer.Ordinal)).IsTrue();
        _ = await Assert.That(configuration.Providers["chatgpt"].ModelDefaults["gpt-5.6-sol"].Output.SequenceEqual(
            ["custom"],
            StringComparer.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Explicit_empty_profile_tool_allowlist_overrides_the_predefined_allowlist()
    {
        var configuration = Load(Write("profiles:\n  thinker:\n    allowed_tools: []\n"));

        _ = await Assert.That(configuration.Profiles["thinker"].AllowedTools).IsEmpty();
    }

    [Test]
    public async Task Replace_tag_is_rejected_outside_append_enabled_sequences()
    {
        _ = await Assert.That(() => Load(Write("model: !replace value\n"))).Throws<InvalidDataException>();
        _ = await Assert.That(() => Load(Write("profiles:\n  thinker:\n    allowed_tools: !replace []\n")))
            .Throws<InvalidDataException>();
        _ = await Assert.That(() => Load(Write("""
            sandbox_rules:
              - path: !replace /tmp
                rule: deny_write
            """))).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("build")]
    [Arguments("plan")]
    [Arguments("query")]
    public async Task Default_profile_accepts_only_canonical_foreground_mode_ids(string id)
    {
        var configuration = Load(Write($"default_profile: {id}\n"));

        _ = await Assert.That(configuration.DefaultProfile).IsEqualTo(id);
    }

    [Test]
    [Arguments("explorer")]
    [Arguments("review")]
    [Arguments("worker")]
    [Arguments("thinker")]
    [Arguments("custom")]
    public async Task Default_profile_rejects_profiles_that_are_not_user_selectable(string id) =>
        _ = await Assert.That(() => Load(Write($"default_profile: {id}\n"))).Throws<InvalidDataException>();

    [Test]
    public async Task Default_profile_accepts_any_profile_made_user_selectable()
    {
        var configuration = Load(Write("""
            default_profile: worker
            profiles:
              worker:
                is_user_selectable: true
            """));

        _ = await Assert.That(configuration.DefaultProfile).IsEqualTo("worker");
    }

    [Test]
    public async Task Profile_configuration_rejects_unknown_profile_ids() =>
        _ = await Assert.That(() => Load(Write("profiles:\n  unknown: {}\n")))
            .Throws<InvalidDataException>().WithMessage("profiles.unknown is not supported");

    [Test]
    public async Task Profile_configuration_rejects_the_removed_user_agent_classification() =>
        _ = await Assert.That(() => Load(Write("profiles:\n  build:\n    is_user_agent: true\n")))
            .Throws<InvalidDataException>();

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
        _ = await Assert.That(profile.ReadOnly).IsTrue();
        _ = await Assert.That(profile.SandboxRules).IsEmpty();
    }

    [Test]
    public async Task Profile_selectability_flags_are_independent_and_partial_overrides_preserve_defaults()
    {
        var configuration = Load(Write("""
            profiles:
              build:
                is_agent_selectable: true
              worker:
                is_user_selectable: true
            """));

        _ = await Assert.That(configuration.Profiles["build"].IsUserSelectable).IsTrue();
        _ = await Assert.That(configuration.Profiles["build"].IsAgentSelectable).IsTrue();
        _ = await Assert.That(configuration.Profiles["worker"].IsUserSelectable).IsTrue();
        _ = await Assert.That(configuration.Profiles["worker"].IsAgentSelectable).IsTrue();
        _ = await Assert.That(configuration.Profiles["query"].IsUserSelectable).IsTrue();
        _ = await Assert.That(configuration.Profiles["query"].IsAgentSelectable).IsFalse();
    }

    [Test]
    [Arguments("is_user_selectable: yes")]
    [Arguments("is_user_selectable: 1")]
    [Arguments("is_agent_selectable: yes")]
    [Arguments("is_agent_selectable: 1")]
    public async Task Profile_selectability_flags_require_strict_booleans(string field) =>
        _ = await Assert.That(() => Load(Write($"profiles:\n  worker:\n    {field}\n")))
            .Throws<InvalidDataException>();

    [Test]
    public async Task Profile_active_work_completion_policy_can_be_overridden()
    {
        var configuration = Load(Write("profiles:\n  query:\n    enforce_active_work_completion: false\n"));

        _ = await Assert.That(configuration.Profiles["query"].EnforceActiveWorkCompletion).IsFalse();
        _ = await Assert.That(configuration.Profiles["build"].EnforceActiveWorkCompletion).IsTrue();
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

        var invalidCompletionPolicy = Write(
            "profiles:\n  query:\n    enforce_active_work_completion: yes\n");
        _ = await Assert.That(() => Load(invalidCompletionPolicy)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Retired_profile_status_is_ignored_at_the_configuration_boundary()
    {
        var configuration = Load(Write("profiles:\n  query:\n    status: retired\n"));

        _ = await Assert.That(configuration.Profiles["query"].Prompt).Contains("query mode");
    }

    [Test]
    [Arguments("profiles:\n  query:\n    read_ony: true\n")]
    [Arguments("profiles:\n  build:\n    hard_rules: []\n")]
    [Arguments("sandbox_rules:\n  - path: /workspace\n    rules: allow_write\n")]
    public async Task Security_configuration_rejects_unknown_keys(string content)
    {
        var path = Write(content);

        _ = await Assert.That(() => Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("profiles:\n  build:\n    prompt: ''\n")]
    [Arguments("profiles:\n  build:\n    max_turns: 0\n")]
    [Arguments("profiles:\n  build:\n    max_turns: -1\n")]
    [Arguments("profiles:\n  build:\n    max_turns: not-a-number\n")]
    [Arguments("profiles:\n  build:\n    max_turns: 1.5\n")]
    public async Task Profile_configuration_rejects_invalid_required_fields(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

    [Test]
    public async Task Provider_model_alias_defaults_are_validated_after_layering()
    {
        var defaults = Load(Write("""
            provider_model_alias_defaults:
              chatgpt:
                low_llm: chatgpt/custom-low
              local:
                low_llm: local/low
                medium_llm: local/medium
                high_llm: local/high
                xhigh_llm: local/xhigh
            """)).ProviderModelAliasDefaults;

        _ = await Assert.That(defaults["chatgpt"].LowModelString).IsEqualTo("chatgpt/custom-low");
        _ = await Assert.That(defaults["chatgpt"].XHighModelString).IsEqualTo("chatgpt/gpt-5.6-sol/xhigh");
        _ = await Assert.That(defaults["local"]).IsEqualTo(new ProviderModelAliasDefaults(
            "local", "local/low", "local/medium", "local/high", "local/xhigh"));
    }

    [Test]
    [Arguments("provider_model_alias_defaults: []\n")]
    [Arguments("provider_model_alias_defaults:\n  local:\n    low_llm: local/low\n")]
    [Arguments("provider_model_alias_defaults:\n  local:\n    low_llm: local/low\n    medium_llm: local/medium\n    high_llm: local/high\n    xhigh_llm: other/xhigh\n")]
    [Arguments("provider_model_alias_defaults:\n  local:\n    low_llm: local/low\n    medium_llm: local/medium\n    high_llm: local/high\n    xhigh_llm: local/xhigh\n    extra: local/extra\n")]
    [Arguments("provider_model_alias_defaults:\n  ' local ':\n    low_llm: local/low\n    medium_llm: local/medium\n    high_llm: local/high\n    xhigh_llm: local/xhigh\n")]
    public async Task Invalid_provider_model_alias_defaults_are_rejected(string content) =>
        _ = await Assert.That(() => Load(Write(content))).Throws<InvalidDataException>();

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
            "Explicit reversible mechanical or evidence work with failure-specific validation; never judgmental review.",
            null,
            new ModelAliasIconConfig("◆", "gray")));
        _ = await Assert.That(aliases["xhigh_llm"]).IsEqualTo(new ModelAliasConfig(
            string.Empty,
            "Specialized strategic work",
            null,
            new ModelAliasIconConfig("◆", "red")));
        _ = await Assert.That(aliases["local"]).IsEqualTo(new ModelAliasConfig(
            "ollama/qwen3", "Local implementation work", "Use local tools first.", null));
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
    public async Task Model_alias_icons_support_custom_graphemes_and_explicit_disabling()
    {
        var aliases = Load(Write("""
            model_aliases:
              low_llm:
                icon:
                  glyph: 🦜
                  color: green
              medium_llm:
                icon: null
              high_llm:
                icon: ""
              xhigh_llm:
                icon:
                  glyph: ""
                  color: red
            """)).ModelAliases;

        _ = await Assert.That(aliases["low_llm"].Icon).IsEqualTo(new ModelAliasIconConfig("🦜", "green"));
        _ = await Assert.That(aliases["medium_llm"].Icon).IsNull();
        _ = await Assert.That(aliases["high_llm"].Icon).IsNull();
        _ = await Assert.That(aliases["xhigh_llm"].Icon).IsNull();
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
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    icon: ◆\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    icon:\n      glyph: ◆\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    icon:\n      glyph: ◆\n      color: orange\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    icon:\n      glyph: ab\n      color: blue\n")]
    [Arguments("model_aliases:\n  custom:\n    usage: A usage\n    icon:\n      glyph: ◆\n      color: blue\n      extra: value\n")]
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
            "openai/gpt-5.6", "Fast local work", null, new ModelAliasIconConfig("◆", "gray")));
        _ = await Assert.That(rewritten).Contains("theme: dark");
    }

    [Test]
    public async Task Set_model_aliases_persists_all_standard_targets_once_and_preserves_other_yaml()
    {
        var path = Write("""
            theme: dark
            model_aliases:
              low_llm:
                usage: Customized low usage
                augment_system_prompt: Keep this metadata.
              custom:
                model_string: local/custom
                usage: Custom work
            """);
        var configuration = Load(path);
        var defaults = configuration.ProviderModelAliasDefaults["chatgpt"];

        configuration.SetModelAliases(defaults);

        var reloaded = Load(path);
        var rewritten = await File.ReadAllTextAsync(path);
        _ = await Assert.That(configuration.ModelAliases["low_llm"].ModelString)
            .IsEqualTo("chatgpt/gpt-5.6-luna/medium");
        _ = await Assert.That(reloaded.ModelAliases["medium_llm"].ModelString)
            .IsEqualTo("chatgpt/gpt-5.6-terra/medium");
        _ = await Assert.That(reloaded.ModelAliases["high_llm"].ModelString)
            .IsEqualTo("chatgpt/gpt-5.6-sol/medium");
        _ = await Assert.That(reloaded.ModelAliases["xhigh_llm"].ModelString)
            .IsEqualTo("chatgpt/gpt-5.6-sol/xhigh");
        _ = await Assert.That(reloaded.ModelAliases["low_llm"].Usage).IsEqualTo("Customized low usage");
        _ = await Assert.That(reloaded.ModelAliases["custom"].ModelString).IsEqualTo("local/custom");
        _ = await Assert.That(rewritten).Contains("theme: dark");
        _ = await Assert.That(rewritten).Contains("augment_system_prompt: Keep this metadata.");
        _ = await Assert.That(rewritten.Split("model_string: chatgpt/", StringSplitOptions.None).Length)
            .IsEqualTo(5);
    }

    [Test]
    public async Task Set_model_aliases_rejects_mismatched_provider_without_changing_memory_or_disk()
    {
        var path = Write("theme: dark\n");
        var configuration = Load(path);
        var before = await File.ReadAllTextAsync(path);

        _ = await Assert.That(() => configuration.SetModelAliases(new ProviderModelAliasDefaults(
            "chatgpt",
            "chatgpt/low",
            "chatgpt/medium",
            "other/high",
            "chatgpt/xhigh"))).Throws<InvalidDataException>();
        _ = await Assert.That(configuration.ModelAliases["low_llm"].ModelString).IsEmpty();
        _ = await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(before);
    }

    [Test]
    public async Task Set_model_aliases_does_not_change_memory_when_persistence_fails()
    {
        var path = Path.Combine(_directory, "config.yaml");
        var configuration = Load(path);
        var defaults = configuration.ProviderModelAliasDefaults["chatgpt"];
        _ = Directory.CreateDirectory(path);

        _ = await Assert.That(() => configuration.SetModelAliases(defaults)).Throws<IOException>();
        _ = await Assert.That(configuration.ModelAliases["low_llm"].ModelString).IsEmpty();
        _ = await Assert.That(configuration.ModelAliases["medium_llm"].ModelString).IsEmpty();
        _ = await Assert.That(configuration.ModelAliases["high_llm"].ModelString).IsEmpty();
        _ = await Assert.That(configuration.ModelAliases["xhigh_llm"].ModelString).IsEmpty();
    }

    [Test]
    public async Task User_configuration_recursively_overrides_predefined_mappings(CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(_directory, "workspace");
        var path = Write($"""
            model_aliases:
              low_llm:
                model_string: openai/gpt-5
            profiles:
              build:
                sandbox_rules:
                  - path: {workspace}
                    rule: allow_write
                    create_if_not_exist: true
            """);
        var configuration = Load(path);

        _ = await Assert.That(configuration.ModelAliases["low_llm"]).IsEqualTo(new ModelAliasConfig(
            "openai/gpt-5",
            "Explicit reversible mechanical or evidence work with failure-specific validation; never judgmental review.",
            null,
            new ModelAliasIconConfig("◆", "gray")));
        _ = await Assert.That(configuration.Profiles["build"].ReadOnly).IsFalse();
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules.Count).IsEqualTo(1);
        _ = await Assert.That(configuration.Profiles["build"].SandboxRules[0])
            .IsEqualTo(new SandboxRule(workspace, SandboxRuleAction.AllowWrite));
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
    public async Task Settings_preserve_replace_tags(CancellationToken cancellationToken)
    {
        var path = Write("cli_utilities:\n  expected: !replace [custom]\n");
        var configuration = Load(path);

        configuration.SetModel("glm-5.2");

        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).Contains("!replace");
        _ = await Assert.That(Load(path).CliUtilities.Expected.SequenceEqual(["custom"], StringComparer.Ordinal))
            .IsTrue();
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

    [Test]
    public async Task Tool_definitions_are_loaded_and_user_overrides_replace_structure_and_prose()
    {
        var path = Write(
            """
            tools:
              question:
                parameters:
                  properties:
                    questions:
                      minItems: 2
                      items:
                        properties:
                          prompt:
                            description: Custom question prompt.
                  required: [questions, custom]
            """);

        var definitions = Load(path).ToolDefinitions.Definitions;

        _ = await Assert.That(definitions.Count).IsEqualTo(25);
        _ = await Assert.That(definitions).ContainsKey("set_exit_reminder");
        using var exitReminder = JsonDocument.Parse(definitions["set_exit_reminder"].ParametersJson);
        _ = await Assert.That(exitReminder.RootElement.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        _ = await Assert.That(exitReminder.RootElement.GetProperty("properties").GetProperty("reminder").GetProperty("type").GetString()).IsEqualTo("string");
        _ = await Assert.That(exitReminder.RootElement.GetProperty("properties").GetProperty("reminder").GetProperty("nullable").GetBoolean()).IsTrue();
        _ = await Assert.That(definitions["question"].Description)
            .StartsWith("Ask ordered questions");
        using var question = JsonDocument.Parse(definitions["question"].ParametersJson);
        var schema = question.RootElement;
        _ = await Assert.That(schema.GetProperty("properties").GetProperty("questions")
            .GetProperty("minItems").GetInt32()).IsEqualTo(2);
        _ = await Assert.That(schema.GetProperty("properties").GetProperty("questions").GetProperty("items")
            .GetProperty("properties").GetProperty("prompt").GetProperty("description").GetString())
            .IsEqualTo("Custom question prompt.");
        _ = await Assert.That(string.Join(",", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("questions,custom");
        using var status = JsonDocument.Parse(definitions["status"].ParametersJson);
        _ = await Assert.That(status.RootElement.GetProperty("type").GetString()).IsEqualTo("object");
    }

    [Test]
    public async Task Question_tools_have_strict_complete_schemas_and_thinker_exposes_both_tools()
    {
        var configuration = Load(Path.Combine(_directory, "missing.yaml"));
        var definitions = configuration.ToolDefinitions.Definitions;

        using var question = JsonDocument.Parse(definitions["question"].ParametersJson);
        using var answer = JsonDocument.Parse(definitions["answer"].ParametersJson);
        var questionSchema = question.RootElement;
        var answerSchema = answer.RootElement;

        _ = await Assert.That(questionSchema.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        _ = await Assert.That(string.Join(",", questionSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("questions");
        _ = await Assert.That(answerSchema.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        _ = await Assert.That(string.Join(",", answerSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("agent_session_id,answers");
        var answerItem = answerSchema.GetProperty("properties").GetProperty("answers").GetProperty("items");
        _ = await Assert.That(answerItem.GetProperty("type").GetString()).IsEqualTo("string");
        _ = await Assert.That(answerItem.GetProperty("minLength").GetInt32()).IsEqualTo(1);
        _ = await Assert.That(configuration.Profiles["thinker"].AllowedTools)
            .Contains("question").And.Contains("answer");
    }

    [Test]
    public async Task Tool_schema_numbers_and_semantic_description_fields_are_preserved()
    {
        var path = Write(
            """
            tools:
              read:
                parameters:
                  properties:
                    offset:
                      maximum: 1e100
                      default:
                        description: ''
            """);

        using var schema = JsonDocument.Parse(
            Load(path).ToolDefinitions.Definitions["read"].ParametersJson);
        var offset = schema.RootElement.GetProperty("properties").GetProperty("offset");
        _ = await Assert.That(offset.GetProperty("maximum").GetRawText()).IsEqualTo("1e100");
        _ = await Assert.That(offset.GetProperty("default").GetProperty("description").GetString())
            .IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("description: ''", "tools.read.description must be a non-empty string")]
    [Arguments("description: null", "tools.read.description must be a non-empty string")]
    [Arguments("parameters: []", "tools.read.parameters must be a mapping")]
    [Arguments("unsupported: value", "tools.read contains an unsupported key")]
    public async Task Invalid_tool_definition_overrides_are_rejected(string overrideYaml, string message)
    {
        var path = Write($"tools:\n  read:\n    {overrideYaml}\n");

        var exception = Assert.Throws<InvalidDataException>(() => Load(path));

        _ = await Assert.That(exception.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Prompt_templates_merge_and_render_multiline_values_without_reparsing_braces()
    {
        var configuration = Load(Write("""
            prompt_templates:
              agent-session.child-completion:
                template: |-
                  Agent {agent_name} says:
                  {result}
            """));

        var rendered = configuration.PromptTemplates.Render(
            "agent-session.child-completion",
            [new("agent_name", "worker"), new("result", "value {not_a_placeholder}\nnext")]);

        _ = await Assert.That(rendered).IsEqualTo("Agent worker says:\nvalue {not_a_placeholder}\nnext");
        _ = await Assert.That(configuration.PromptTemplates.Render(
            "tool-result.output-blob-notice",
            [new("path", "/tmp/{runtime}")])).Contains("/tmp/{runtime}; metadata: {literal}");
    }

    [Test]
    [Arguments("template: '{unknown}'", "unknown placeholder")]
    [Arguments("template: '{path} {path}'", "duplicate placeholder")]
    [Arguments("template: ''", "must be a non-empty string")]
    [Arguments("required_arguments: [path, absent]", "must be included in allowed_arguments")]
    public async Task Invalid_prompt_template_overrides_report_the_configuration_path(string overrideYaml, string reason)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Load(Write($"""
            prompt_templates:
              tool-result.output-blob-notice:
                {overrideYaml}
            """)));

        _ = await Assert.That(exception.Message).StartsWith("prompt_templates.tool-result.output-blob-notice");
        _ = await Assert.That(exception.Message).Contains(reason);
    }

    [Test]
    public async Task Prompt_template_render_arguments_are_validated()
    {
        var templates = Load(Write(string.Empty)).PromptTemplates;

        var missing = Assert.Throws<InvalidDataException>(() => templates.Render(
            "system.working-directory", []));
        var unknown = Assert.Throws<InvalidDataException>(() => templates.Render(
            "system.working-directory", [new("other", "value")]));
        var duplicate = Assert.Throws<InvalidDataException>(() => templates.Render(
            "system.working-directory", [new("working_directory", "a"), new("working_directory", "b")]));

        _ = await Assert.That(missing.Message).Contains("requires argument 'working_directory'");
        _ = await Assert.That(unknown.Message).Contains("does not allow argument 'other'");
        _ = await Assert.That(duplicate.Message).Contains("duplicate argument 'working_directory'");
    }

    private Configuration Load(string path) =>
        Configuration.Load(path, Path.Combine(_directory, "predefined_config.yaml"));

    private Configuration Load(string path, IReadOnlyDictionary<string, string> environment) =>
        Configuration.Load(path, Path.Combine(_directory, "predefined_config.yaml"), environment);

    private string Write(string content)
    {
        _ = Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
