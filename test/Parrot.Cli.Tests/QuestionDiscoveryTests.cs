using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class QuestionDiscoveryTests
{
    [Test]
    [Arguments(false, "attach")]
    [Arguments(true, "attach")]
    [Arguments(false, "late")]
    [Arguments(true, "late")]
    [Arguments(false, "switch")]
    [Arguments(true, "switch")]
    public async Task Pending_questions_are_discovered_without_a_new_tool_event(
        bool enhanced,
        string scenario,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var sessionId = "resumed-session";
        var request = new EnhancedChatRequest(new CreateSessionRequest(), string.Empty)
        {
            InitialSession = new UserSession { Id = sessionId, Model = "provider/model", Mode = "build", Loaded = true },
        };
        using var driver = new CliLifecycleDriver(enhanced, request);
        var pending = new PendingQuestion
        {
            Id = "pending-question",
            Questions = { new QuestionDefinition { Header = "Decision", Prompt = "Discover this question", Options = { "One" } } },
        };
        if (scenario == "attach")
        {
            driver.Invoker.AddPendingQuestion(sessionId, pending);
        }

        var running = driver.Drive(token);
        try
        {
            if (scenario == "late")
            {
                driver.Input.Type("ask me");
                await driver.Sent(1, token);
                await driver.Invoker.Publish(sessionId, new Event
                {
                    ToolStarted = new ToolStarted { ToolCallId = "question-call", ToolName = "question" },
                });
                await Task.Delay(800, token);
                driver.Invoker.AddPendingQuestion(sessionId, pending);
            }
            else if (scenario == "switch")
            {
                // The acquired initial session does not consume a CreateSession call.
                sessionId = "session-1";
                driver.Invoker.AddPendingQuestion(sessionId, pending);
                driver.Input.Type("/clear");
                await driver.OutputContains("Select a provider", token);
                driver.Input.Type("provider");
                driver.Input.Type("model");
                driver.Input.Type("query");
                await driver.OutputContains("new session session-1", token);
                if (enhanced)
                {
                    driver.Input.Type(string.Empty);
                }
            }

            await driver.OutputContains("Discover this question", token);
            driver.Input.Type(enhanced ? string.Empty : "one");
            while (driver.Invoker.QuestionReplies.Count == 0)
            {
                await Task.Delay(5, token);
            }

            _ = await Assert.That(driver.Invoker.QuestionReplies).HasSingleItem();
            var reply = driver.Invoker.QuestionReplies.Single();
            _ = await Assert.That(reply.UserSessionId).IsEqualTo(sessionId);
            _ = await Assert.That(reply.QuestionRequestId).IsEqualTo(pending.Id);
            _ = await Assert.That(reply.Answers.Single().Text).IsEqualTo(enhanced ? "One" : "one");
            driver.Input.Type("/exit");
            _ = await Assert.That(await running.WaitAsync(token)).IsEqualTo(CommandDispatcher.ExitSuccess);
        }
        finally
        {
            await timeout.CancelAsync();
            try
            {
                _ = await running;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
            }
        }
    }
}
