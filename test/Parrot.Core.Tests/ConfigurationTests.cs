using Parrot.Config;

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
