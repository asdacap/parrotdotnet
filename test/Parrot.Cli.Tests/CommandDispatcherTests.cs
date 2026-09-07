using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Process;
using Parrot.State;

namespace Parrot.Cli.Tests;

internal sealed class CommandDispatcherTests
{
    // No `[Arguments("")]`: empty args route to `chat`, which needs a provider
    // and cannot run here. Bare `parrot` opening a session is verified live.
    [Test]
    [Arguments("help", CommandDispatcher.ExitSuccess, "Usage:")]
    [Arguments("--help", CommandDispatcher.ExitSuccess, "Commands:")]
    [Arguments("-h", CommandDispatcher.ExitSuccess, "Commands:")]
    [Arguments("version", CommandDispatcher.ExitSuccess, "parrot ")]
    [Arguments("--version", CommandDispatcher.ExitSuccess, "parrot ")]
    public async Task Recognised_command_writes_to_output_and_succeeds(
        string argument, int expectedExitCode, string expectedFragment, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stopping = new CancellationTokenSource();
        using var workspace = new TestWorkspace();
        using var diagnostics = new DiagnosticLogs(workspace.Paths, FileDiagnosticLog.CreateInstanceId(), error, TimeProvider.System);
        using var composition = new CommandComposition(new Interrupts(stopping), output, error, diagnostics);

        var exitCode = await composition.Dispatcher.Run(argument.Length == 0 ? [] : [argument], cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(expectedExitCode);
        _ = await Assert.That(output.ToString()).Contains(expectedFragment);
        _ = await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    public async Task Command_composition_does_not_close_process_diagnostics(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stopping = new CancellationTokenSource();
        using var workspace = new TestWorkspace();
        using var diagnostics = new DiagnosticLogs(workspace.Paths, FileDiagnosticLog.CreateInstanceId(), error, TimeProvider.System);
        using (var composition = new CommandComposition(new Interrupts(stopping), output, error, diagnostics))
        {
            _ = await composition.Dispatcher.Run(["version"], cancellationToken);
        }

        diagnostics.Global.Write(new DiagnosticEvent("process", "after_composition", DiagnosticSeverity.Information));
        var file = Directory.GetFiles(workspace.Paths.LogDirectory).Single();
        _ = await Assert.That(await File.ReadAllTextAsync(file, cancellationToken)).Contains("event=\"after_composition\"");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Variant_without_a_value_reports_usage_error(
        bool followedByFlag, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stopping = new CancellationTokenSource();
        using var workspace = new TestWorkspace();
        using var diagnostics = new DiagnosticLogs(workspace.Paths, FileDiagnosticLog.CreateInstanceId(), error, TimeProvider.System);
        using var composition = new CommandComposition(new Interrupts(stopping), output, error, diagnostics);
        var arguments = followedByFlag ? new[] { "chat", "--variant", "--basic" } : ["chat", "--variant"];

        var exitCode = await composition.Dispatcher.Run(arguments, cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitUsage);
        _ = await Assert.That(output.ToString()).IsEmpty();
        _ = await Assert.That(error.ToString()).Contains("--variant <name>");
    }

    [Test]
    [Arguments("nonsense")]
    [Arguments("--nonsense")]
    public async Task Unknown_command_reports_usage_error_on_stderr(string argument, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var stopping = new CancellationTokenSource();
        using var workspace = new TestWorkspace();
        using var diagnostics = new DiagnosticLogs(workspace.Paths, FileDiagnosticLog.CreateInstanceId(), error, TimeProvider.System);
        using var composition = new CommandComposition(new Interrupts(stopping), output, error, diagnostics);

        var exitCode = await composition.Dispatcher.Run(argument.Length == 0 ? [] : [argument], cancellationToken);

        _ = await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitUsage);
        _ = await Assert.That(output.ToString()).IsEmpty();
        _ = await Assert.That(error.ToString()).Contains(argument);
    }

    [Test]
    public async Task Cli_utility_warning_is_exact_sorted_and_empty_when_expected_commands_are_available()
    {
        var missing = CliUtilityAvailability.Inspect(
            new CliUtilityCandidates(["zeta", "alpha"], []),
            new ExecutableLocator(string.Empty, string.Empty));
        var available = CliUtilityAvailability.Inspect(
            new CliUtilityCandidates([], ["optional"]),
            new ExecutableLocator(string.Empty, string.Empty));

        _ = await Assert.That(CommandDispatcher.CliUtilityWarning(missing)).IsEqualTo(
            "warning: expected CLI utilities are unavailable: alpha, zeta; Bash shell commands may fail");
        _ = await Assert.That(CommandDispatcher.CliUtilityWarning(available)).IsEmpty();
    }

    private sealed class TestWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-command-tests-" + Guid.NewGuid().ToString("N"));

        public TestWorkspace() => Paths = new StatePaths(_root, _root, _root);

        public StatePaths Paths { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
