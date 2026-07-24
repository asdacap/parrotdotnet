using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

// The client half of the queue: what the driver does with a line typed while a
// turn is still streaming.
//
// Everything the driver holds for the length of the loop is held here too, and
// released when the test ends -- the loop outlives any single statement in the
// test, so nothing it uses can be scoped to one.
internal sealed class CliDriverTests : IDisposable
{
    private readonly ScriptedInvoker _invoker = new();
    private readonly ScriptedInput _input = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _stopping = new();

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
        _error.Dispose();
        _http.Dispose();
        _stopping.Dispose();
    }

    [Test]
    public async Task A_line_typed_during_a_turn_is_sent_rather_than_held_until_it_ends(
        CancellationToken cancellationToken)
    {
        // A turn that starts and never ends. The old driver blocked in
        // RenderTurn until TurnEnded, so a line typed from here could not be
        // read at all, let alone sent.
        await _invoker.Publish(new Event { Id = "e1", TurnStarted = new TurnStarted { Model = "model" } });

        var driving = Drive(new Interrupts(_stopping), cancellationToken);

        _input.Type("first prompt");
        await Sent(1, cancellationToken);

        _input.Type("typed while working");
        await Sent(2, cancellationToken);

        _input.End();
        _ = await driving;

        _ = await Assert.That(string.Join(" | ", _invoker.Sent))
            .IsEqualTo("first prompt | typed while working");
        _ = await Assert.That(_invoker.Interrupts).IsEqualTo(0);
    }

    [Test]
    public async Task Ctrl_c_stops_the_turn_once_and_then_stops_parrot(CancellationToken cancellationToken)
    {
        var interrupts = new Interrupts(_stopping);

        await _invoker.Publish(new Event { Id = "e1", TurnStarted = new TurnStarted { Model = "model" } });

        var driving = Drive(interrupts, cancellationToken);

        _input.Type("first prompt");
        await Sent(1, cancellationToken);

        // The first one is the turn's, and the process is left alone.
        interrupts.Signal();

        while (_invoker.Interrupts < 1)
        {
            await Task.Delay(5, cancellationToken);
        }

        _ = await Assert.That(_stopping.IsCancellationRequested).IsFalse();

        // The second is not: the request is still outstanding, so asking again
        // is asking for something else.
        interrupts.Signal();

        _ = await Assert.That(_stopping.IsCancellationRequested).IsTrue();
        _ = await Assert.That(_invoker.Interrupts).IsEqualTo(1);

        _input.End();
        _ = await driving;
    }

    private async Task Sent(int count, CancellationToken cancellationToken)
    {
        while (_invoker.Sent.Count < count)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<int> Drive(Interrupts interrupts, CancellationToken cancellationToken)
    {
        var client = new GeneratedParrot.ParrotClient(_invoker);

        var context = new SlashContext(
            client,
            new UnusedCredentials(),
            new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            "user-session",
            _input,
            _output,
            _error);

        var driver = new CliDriver(
            client, new BasicCli(), new SlashCommandRegistry([new ExitCommand()]), interrupts);

        return driver.Run(context, string.Empty, _input, _output, cancellationToken);
    }
}
