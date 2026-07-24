namespace Parrot.Cli.Tests;

internal sealed class CommandDispatcherTests
{
    [Test]
    [Arguments("", CommandDispatcher.ExitSuccess, "Usage:")]
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

        var exitCode = await CommandDispatcher.Run(ArgumentVector(argument), output, error, cancellationToken);

        await Assert.That(exitCode).IsEqualTo(expectedExitCode);
        await Assert.That(output.ToString()).Contains(expectedFragment);
        await Assert.That(error.ToString()).IsEmpty();
    }

    [Test]
    [Arguments("nonsense")]
    [Arguments("--nonsense")]
    public async Task Unknown_command_reports_usage_error_on_stderr(string argument, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await CommandDispatcher.Run(ArgumentVector(argument), output, error, cancellationToken);

        await Assert.That(exitCode).IsEqualTo(CommandDispatcher.ExitUsage);
        await Assert.That(output.ToString()).IsEmpty();
        await Assert.That(error.ToString()).Contains(argument);
    }

    private static string[] ArgumentVector(string argument) =>
        argument.Length == 0 ? [] : [argument];
}
