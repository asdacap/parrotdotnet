using Parrot.Config;
using Parrot.Process;

namespace Parrot.Core.Tests;

internal sealed class ExecutableLocatorTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-locator-tests", Guid.NewGuid().ToString("n"));

    public ExecutableLocatorTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Locator_returns_the_first_executable_on_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var first = Directory.CreateDirectory(Path.Combine(_workspace, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_workspace, "second")).FullName;
        var nonExecutable = Path.Combine(first, "utility");
        var executable = Path.Combine(second, "utility");
        await File.WriteAllTextAsync(nonExecutable, string.Empty);
        await File.WriteAllTextAsync(executable, string.Empty);
        File.SetUnixFileMode(nonExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var locator = new ExecutableLocator(
            string.Join(Path.PathSeparator, first, second),
            ".COM;.EXE;.BAT;.CMD");

        _ = await Assert.That(locator.Locate("utility")).IsEqualTo(executable);
    }

    [Test]
    public async Task Locator_does_not_report_a_non_executable_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var utility = Path.Combine(_workspace, "utility");
        await File.WriteAllTextAsync(utility, string.Empty);
        File.SetUnixFileMode(utility, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var locator = new ExecutableLocator(_workspace, ".EXE");

        _ = await Assert.That(locator.Locate("utility")).IsEmpty();
    }

    [Test]
    public async Task Windows_locator_uses_path_ext_for_commands_without_extensions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executable = Path.Combine(_workspace, "utility.CMD");
        await File.WriteAllTextAsync(executable, string.Empty);
        var locator = new ExecutableLocator(_workspace, ".EXE;.CMD");

        _ = await Assert.That(locator.Locate("utility")).IsEqualTo(executable);
    }

    [Test]
    public async Task Empty_path_does_not_search_the_current_directory()
    {
        var command = $"parrot-locator-{Guid.NewGuid():n}";
        var fileName = OperatingSystem.IsWindows() ? command + ".CMD" : command;
        var executable = Path.Combine(Directory.GetCurrentDirectory(), fileName);

        try
        {
            await File.WriteAllTextAsync(executable, string.Empty);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }

            var locator = new ExecutableLocator(string.Empty, ".CMD");
            _ = await Assert.That(locator.Locate(command)).IsEmpty();
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Test]
    public async Task Availability_sorts_and_classifies_commands()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteExecutable("expected-two");
        await WriteExecutable("expected-one");
        await WriteExecutable("optional-two");
        var candidates = new CliUtilityCandidates(
            ["expected-two", "missing-two", "expected-one", "missing-one"],
            ["missing-optional", "optional-two"]);
        var availability = CliUtilityAvailability.Inspect(
            candidates,
            new ExecutableLocator(_workspace, ".EXE"));

        _ = await Assert.That(string.Join(',', availability.AvailableExpected))
            .IsEqualTo("expected-one,expected-two");
        _ = await Assert.That(string.Join(',', availability.MissingExpected))
            .IsEqualTo("missing-one,missing-two");
        _ = await Assert.That(string.Join(',', availability.AvailableOptional))
            .IsEqualTo("optional-two");
        _ = await Assert.That(((ICollection<string>)availability.AvailableExpected).IsReadOnly).IsTrue();
        _ = await Assert.That(((ICollection<string>)availability.MissingExpected).IsReadOnly).IsTrue();
        _ = await Assert.That(((ICollection<string>)availability.AvailableOptional).IsReadOnly).IsTrue();
    }

    [Test]
    public async Task Process_runner_locates_bubblewrap_independently_of_reported_candidates()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        await WriteExecutable("bwrap");
        var candidates = new CliUtilityCandidates(["git"], ["jq"]);
        var locator = new ExecutableLocator(_workspace, ".EXE");
        var availability = CliUtilityAvailability.Inspect(candidates, locator);
        var runner = ProcessRunner.Locate(locator);

        _ = await Assert.That(availability.AvailableExpected).IsEmpty();
        _ = await Assert.That(string.Join(',', availability.MissingExpected)).IsEqualTo("git");
        _ = await Assert.That(runner.SandboxAvailable).IsTrue();
    }

    private async Task WriteExecutable(string name)
    {
        var path = Path.Combine(_workspace, name);
        await File.WriteAllTextAsync(path, string.Empty);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }
}
