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
    public async Task A_missing_file_yields_an_empty_model()
    {
        var configuration = Configuration.Load(Path.Combine(_directory, "config.yaml"));

        _ = await Assert.That(configuration.Model).IsEmpty();
    }

    [Test]
    public async Task The_model_is_read_from_the_file()
    {
        var path = Write("# parrot config\nmodel: deepseek-v4-pro\n");

        _ = await Assert.That(Configuration.Load(path).Model).IsEqualTo("deepseek-v4-pro");
    }

    [Test]
    public async Task Web_fetch_private_access_is_opt_in()
    {
        var missing = Configuration.Load(Path.Combine(_directory, "missing.yaml"));
        var configured = Configuration.Load(Write("web_fetch:\n  allow_private: true\n"));

        _ = await Assert.That(missing.WebFetch.AllowPrivate).IsFalse();
        _ = await Assert.That(configured.WebFetch.AllowPrivate).IsTrue();
    }

    [Test]
    public async Task Security_configuration_is_strict_and_ordered()
    {
        var configuration = Configuration.Load(Write("""
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
        _ = await Assert.That(configuration.Profiles["plan"].ReadOnly).IsNull();
        _ = await Assert.That(configuration.Profiles["query"].ReadOnly).IsTrue();
    }

    [Test]
    public async Task Invalid_security_configuration_fails_closed()
    {
        var relativePath = Write("sandbox_rules:\n  - path: relative\n    rule: allow_write\n");
        _ = await Assert.That(() => Configuration.Load(relativePath)).Throws<InvalidDataException>();

        var invalidAction = Write("sandbox_rules:\n  - path: /workspace\n    rule: unknown\n");
        _ = await Assert.That(() => Configuration.Load(invalidAction)).Throws<InvalidDataException>();

        var invalidProfile = Write("profiles:\n  worker:\n    read_only: true\n");
        _ = await Assert.That(() => Configuration.Load(invalidProfile)).Throws<InvalidDataException>();

        var invalidBoolean = Write("profiles:\n  query:\n    read_only: yes\n");
        _ = await Assert.That(() => Configuration.Load(invalidBoolean)).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("profiles:\n  query:\n    read_ony: true\n")]
    [Arguments("sandbox_rules:\n  - path: /workspace\n    rules: allow_write\n")]
    public async Task Security_configuration_rejects_unknown_keys(string content)
    {
        var path = Write(content);

        _ = await Assert.That(() => Configuration.Load(path)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Set_model_persists_and_survives_a_reload()
    {
        var path = Path.Combine(_directory, "config.yaml");

        Configuration.Load(path).SetModel("glm-5.2");

        _ = await Assert.That(Configuration.Load(path).Model).IsEqualTo("glm-5.2");
    }

    [Test]
    public async Task Set_model_replaces_only_that_key_and_keeps_the_others(CancellationToken cancellationToken)
    {
        var path = Write("model: old\ntheme: dark\n");

        Configuration.Load(path).SetModel("new");

        var rewritten = await File.ReadAllTextAsync(path, cancellationToken);

        _ = await Assert.That(rewritten).Contains("model: new");
        _ = await Assert.That(rewritten).Contains("theme: dark");
        _ = await Assert.That(rewritten).DoesNotContain("old");
    }

    [Test]
    public async Task Set_model_adds_the_key_when_absent(CancellationToken cancellationToken)
    {
        var path = Write("theme: dark\n");

        Configuration.Load(path).SetModel("added");

        _ = await Assert.That(Configuration.Load(path).Model).IsEqualTo("added");
        _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).Contains("theme: dark");
    }

    private string Write(string content)
    {
        _ = Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
