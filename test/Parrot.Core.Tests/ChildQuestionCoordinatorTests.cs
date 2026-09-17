using Parrot.Agent;
using Parrot.Questions;
using Parrot.Store;
using Parrot.Tools;
using Delivery = Parrot.Protocol.Delivery;
using ProtocolEvent = Parrot.Protocol.Event;

namespace Parrot.Core.Tests;

internal sealed partial class SubagentTests
{
    [Test]
    public async Task Child_questions_are_parent_scoped_copied_validated_and_settled_once(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var unrelated = Session(provider, 0, "unrelated", registry, cancellationToken);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var questions = new[] { new QuestionDefinition("Colour", "Pick", [new Parrot.Questions.QuestionOption("Blue", string.Empty)], false, false) };
        var asking = coordinator.Ask(child, questions, cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        await provider.Arrived(cancellationToken);
        var steer = string.Join('\n', provider.Requests.Single().Messages.Select(message => message.Content));

        _ = await Assert.That(steer).Contains($"Child agent {child.Name} is waiting");
        _ = await Assert.That(steer).Contains("Question 1:");
        _ = await Assert.That(steer).Contains("Header: Colour");
        _ = await Assert.That(steer).Contains("Prompt: Pick");
        _ = await Assert.That(steer).Contains("Multiple: false");
        _ = await Assert.That(steer).Contains("Custom: false");
        _ = await Assert.That(steer).Contains("- Blue");
        _ = await Assert.That(_repository.Replay()).Contains(published =>
            published.AgentSessionId == parent.SessionId
            && published.PayloadCase == ProtocolEvent.PayloadOneofCase.InputAdmitted
            && published.InputAdmitted.Delivery == Delivery.Steer);
        provider.Release();
        await parent.DisposeAsync();

        questions[0] = new QuestionDefinition(string.Empty, "Changed", [], false, true);

        _ = await Assert.That(pending.AskingAgentSessionId).IsEqualTo(child.SessionId);
        _ = await Assert.That(pending.AskingAgentName).IsEqualTo(child.Name);
        _ = await Assert.That(pending.ParentAgentSessionId).IsEqualTo(parent.SessionId);
        _ = await Assert.That(pending.Questions.Single().Options.Single().Label).IsEqualTo("Blue");
        _ = await Assert.That(coordinator.PendingForParent(unrelated)).IsEmpty();
        _ = await Assert.That(async () =>
            await coordinator.Ask(child, [new QuestionDefinition("Continue", "duplicate", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken))
            .Throws<QuestionRejectedException>();
        _ = await Assert.That(() => coordinator.ReplyFromParent(
            TestModels.ScopeOf(unrelated).ParentScope,
            child.Name,
            new QuestionReply([new QuestionAnswer("blue")]))).Throws<AgentRegistryException>();
        _ = await Assert.That(() => coordinator.ReplyFromParent(
            TestModels.ScopeOf(parent).ParentScope,
            child.Name,
            new QuestionReply([new QuestionAnswer(string.Empty)]))).Throws<QuestionException>();
        _ = await Assert.That(coordinator.PendingForParent(parent)).Count().IsEqualTo(1);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(
            () =>
            {
                try
                {
                    coordinator.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, child.Name, new QuestionReply([new QuestionAnswer("blue")]));
                    return true;
                }
                catch (QuestionRejectedException)
                {
                    return false;
                }
            },
            cancellationToken)));

        _ = await Assert.That(attempts.Count(static succeeded => succeeded)).IsEqualTo(1);
        _ = await Assert.That((await asking).Answers.Single().Text).IsEqualTo("blue");
        _ = await Assert.That(coordinator.PendingForParent(parent)).IsEmpty();
        _ = await Assert.That(() => coordinator.ReplyFromParent(
            TestModels.ScopeOf(parent).ParentScope,
            child.Name,
            new QuestionReply([new QuestionAnswer("blue")]))).Throws<QuestionRejectedException>();
    }

    [Test]
    public async Task Answer_tool_authorizes_direct_parent_and_invalid_answers_leave_request_pending(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent-tool", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var unrelated = Session(provider, 0, "unrelated-tool", registry, cancellationToken);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "tool-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var asking = coordinator.Ask(child, [new QuestionDefinition("Continue", "continue", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken);
        _ = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        var arguments = $$"""
            {"agent_name":"{{child.Name}}","answers":["yes"]}
            """;
        var invalidArguments = $$"""
            {"agent_name":"{{child.Name}}","answers":[""]}
            """;

        var unauthorized = await new AnswerTool(coordinator, TestModels.ScopeOf(unrelated).ParentScope).Execute(
            new ToolInvocation("unauthorized", arguments),
            new TurnFixture(unrelated, router).Selection,
            cancellationToken);
        var invalid = await new AnswerTool(coordinator, TestModels.ScopeOf(parent).ParentScope).Execute(
            new ToolInvocation("invalid", invalidArguments),
            new TurnFixture(parent, router).Selection,
            cancellationToken);

        _ = await Assert.That(unauthorized.Text).StartsWith("error: child agent not found:");
        _ = await Assert.That(invalid.Text).IsEqualTo("error: question answers cannot be empty");
        _ = await Assert.That(coordinator.PendingForParent(parent)).Count().IsEqualTo(1);

        var answered = await new AnswerTool(coordinator, TestModels.ScopeOf(parent).ParentScope).Execute(
            new ToolInvocation("answered", arguments),
            new TurnFixture(parent, router).Selection,
            cancellationToken);

        _ = await Assert.That(answered.Text).IsEqualTo($"Answered the pending question from child agent {child.Name}.");
        _ = await Assert.That((await asking).Answers.Single().Text).IsEqualTo("yes");
        _ = await Assert.That(coordinator.PendingForParent(parent)).IsEmpty();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await parent.DisposeAsync();
    }

    [Test]
    public async Task Cancellation_disposal_and_user_session_ownership_leave_no_child_question_waiter(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var firstRegistry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var secondRegistry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", firstRegistry, cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", secondRegistry, cancellationToken);
        var firstCoordinator = new ChildQuestionCoordinator(TestModels.ScopeOf(firstParent).ParentScope, TestModels.ScopeOf(firstParent).ChildRegistry, TestModels.PromptTemplates);
        var secondCoordinator = new ChildQuestionCoordinator(TestModels.ScopeOf(secondParent).ParentScope, TestModels.ScopeOf(secondParent).ChildRegistry, TestModels.PromptTemplates);
        var firstChild = TestModels.ScopeOf(firstParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            firstParent,
            new TurnFixture(firstParent, router).Selection,
            "worker",
            firstParent.CurrentSelection().RequestedModel,
            "first-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var secondChild = TestModels.ScopeOf(secondParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            secondParent,
            new TurnFixture(secondParent, router).Selection,
            "worker",
            secondParent.CurrentSelection().RequestedModel,
            "second-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = firstCoordinator.Ask(firstChild, [new QuestionDefinition("Continue", "cancelled", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], stopping.Token);
        _ = await WaitForChildQuestion(firstCoordinator, firstParent, cancellationToken);
        var disposed = firstCoordinator.Ask(
            TestModels.ScopeOf(firstParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            firstParent,
            new TurnFixture(firstParent, router).Selection,
            "worker",
            firstParent.CurrentSelection().RequestedModel,
            "disposed-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session,
            [new QuestionDefinition("Continue", "disposed", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)],
            cancellationToken);
        var isolated = secondCoordinator.Ask(secondChild, [new QuestionDefinition("Continue", "isolated", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken);
        _ = await WaitForChildQuestion(secondCoordinator, secondParent, cancellationToken);

        await stopping.CancelAsync();
        _ = await Assert.That(cancelled).Throws<OperationCanceledException>();
        _ = await Assert.That(firstCoordinator.PendingForParent(firstParent)).Count().IsEqualTo(1);
        _ = await Assert.That(secondCoordinator.PendingForParent(firstParent)).IsEmpty();

        firstCoordinator.Dispose();
        firstCoordinator.Dispose();
        _ = await Assert.That(disposed).Throws<QuestionRejectedException>();
        _ = await Assert.That(firstCoordinator.PendingForParent(firstParent)).IsEmpty();
        _ = await Assert.That(async () =>
            await firstCoordinator.Ask(firstChild, [new QuestionDefinition("Continue", "late", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken))
            .Throws<ObjectDisposedException>();

        secondCoordinator.ReplyFromParent(TestModels.ScopeOf(secondParent).ParentScope, secondChild.Name, new QuestionReply([new QuestionAnswer("yes")]));
        _ = await isolated;

        await TestModels.ScopeOf(firstParent).DisposeAsync();
        await TestModels.ScopeOf(secondParent).DisposeAsync();
    }

    [Test]
    [Arguments("not-json")]
    [Arguments("{}")]
    [Arguments("{\"agent_name\":\"{0}\"}")]
    [Arguments("{\"agent_name\":\"{0}\",\"answers\":[]}")]
    [Arguments("{\"agent_name\":\"{0}\",\"answers\":[null]}")]
    [Arguments("{\"agent_name\":\"{0}\",\"answers\":[\"\"]}")]
    [Arguments("{\"agent_name\":\"{0}\",\"answers\":[\"yes\",\"no\"]}")]
    [Arguments("{\"agent_name\":\"{0}\",\"answers\":[{\"text\":\"yes\"}]}")]
    [Arguments("{\"agent_name\":\"{0}\",\"answers\":[\"yes\"],\"extra\":true}")]
    public async Task Malformed_incomplete_duplicate_and_stale_answers_leave_child_request_pending(
        string argumentsTemplate,
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "answer-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "answer-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var asking = coordinator.Ask(child, [new QuestionDefinition("Continue", "continue", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        var arguments = argumentsTemplate.Replace("{0}", child.Name, StringComparison.Ordinal);

        var result = await new AnswerTool(coordinator, TestModels.ScopeOf(parent).ParentScope).Execute(
            new ToolInvocation("malformed", arguments),
            new TurnFixture(parent, router).Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("error:");
        _ = await Assert.That(coordinator.PendingForParent(parent)).HasSingleItem();
        coordinator.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, child.Name, new QuestionReply([new QuestionAnswer("yes")]));
        _ = await Assert.That((await asking).Answers.Single().Text).IsEqualTo("yes");
        _ = await Assert.That(() => coordinator.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, child.Name, new QuestionReply([new QuestionAnswer("yes")])))
            .Throws<QuestionRejectedException>();
        _ = await Assert.That(pending.Id).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task Nested_child_questions_use_the_immediate_parent_without_routing_to_the_root(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "question-root", registry, cancellationToken);
        var parent = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            new TurnFixture(root, router).Selection,
            "worker",
            root.CurrentSelection().RequestedModel,
            "question-parent",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "question-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;

        var asking = coordinator.Ask(child, [new QuestionDefinition("Continue", "nested", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(pending.ParentAgentSessionId).IsEqualTo(parent.SessionId);
        _ = await Assert.That(provider.Requests).HasSingleItem();
        _ = await Assert.That(provider.Requests.Single().Messages.Select(message => message.Content))
            .Contains(content => content.Contains(child.Name, StringComparison.Ordinal));
        _ = await Assert.That(coordinator.PendingForParent(root)).IsEmpty();

        coordinator.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, child.Name, new QuestionReply([new QuestionAnswer("yes")]));
        provider.Release();
        _ = await Assert.That((await asking).Answers.Single().Text).IsEqualTo("yes");
        await parent.DisposeAsync();
        _ = await Assert.That(provider.Requests).HasSingleItem();
    }

    [Test]
    public async Task Multiple_children_are_pending_independently_and_are_all_listed_in_completion_reminder(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "multiple-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var first = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "first-question-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var second = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "second-question-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;

        var firstAsking = coordinator.Ask(first, [new QuestionDefinition("Continue", "first", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken);
        var secondAsking = coordinator.Ask(second, [new QuestionDefinition("Continue", "second", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], cancellationToken);
        _ = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        _ = await Assert.That(coordinator.PendingForParent(parent)).Count().IsEqualTo(2);
        await provider.Arrived(cancellationToken);

        var reminder = coordinator.BeginParentCompletion(parent);
        _ = await Assert.That(reminder.Reminder).IsNotNull();
        _ = await Assert.That(reminder.Reminder).Contains(first.Name);
        _ = await Assert.That(reminder.Reminder).Contains(second.Name);
        reminder.Dispose();
        coordinator.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, first.Name, new QuestionReply([new QuestionAnswer("yes")]));
        coordinator.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, second.Name, new QuestionReply([new QuestionAnswer("yes")]));
        _ = await Task.WhenAll(firstAsking, secondAsking);
        provider.Release();
        await parent.DisposeAsync();
    }

    [Test]
    public async Task Question_factory_keeps_root_questions_in_the_user_broker_and_children_out_of_it(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        using var userQuestions = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System, TestDiagnosticLog.Instance);
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "factory-root", registry, cancellationToken);
        var rootScope = _rootScopes[^1];
        var child = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            new TurnFixture(root, router).Selection,
            "worker",
            root.CurrentSelection().RequestedModel,
            "factory-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var childQuestions = rootScope.ChildQuestions;
        var rootFactory = new QuestionToolFactory(userQuestions, AgentSessionParentScope.Root(), TestModels.ToolDefinitions);
        var childFactory = new QuestionToolFactory(userQuestions, AgentSessionParentScope.Child(rootScope, AgentCompletionDeliveryPolicy.RetainedOnly), TestModels.ToolDefinitions);
        const string request = "{\"questions\":[{\"prompt\":\"Choose\",\"options\":[\"Yes\"]}]}";

        var rootExecution = rootFactory.Create(root).Execute(
            new ToolInvocation("root-question", request),
            new TurnFixture(root, router).Selection,
            cancellationToken);
        _ = await Assert.That(userQuestions.Pending()).HasSingleItem();
        _ = await Assert.That(childQuestions.PendingForParent(root)).IsEmpty();
        userQuestions.Reply(userQuestions.Pending().Single().Id, new QuestionReply(
            [new QuestionAnswer("yes")]));
        _ = await rootExecution;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var childExecution = childFactory.Create(child).Execute(
            new ToolInvocation("child-question", request),
            new TurnFixture(child, router).Selection,
            stopping.Token);
        _ = await Assert.That(childQuestions.PendingForParent(root)).HasSingleItem();
        _ = await Assert.That(userQuestions.Pending()).IsEmpty();
        await stopping.CancelAsync();
        _ = await Assert.That(childExecution).Throws<OperationCanceledException>();
        _ = await Assert.That(childQuestions.PendingForParent(root)).IsEmpty();
        provider.Release();
        await root.DisposeAsync();
    }

    [Test]
    public async Task Cancellation_while_waiting_for_parent_completion_reservation_is_deterministic(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = new RouterFixture(provider, []).Router;
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "reservation-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "reservation-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reservation = coordinator.BeginParentCompletion(parent);
        var asking = coordinator.Ask(child, [new QuestionDefinition("Continue", "reserved", [new Parrot.Questions.QuestionOption("Yes", string.Empty)], false, false)], stopping.Token);

        await stopping.CancelAsync();
        _ = await Assert.That(asking).Throws<OperationCanceledException>();
        _ = await Assert.That(coordinator.PendingForParent(parent)).IsEmpty();
    }

    private static async Task<PendingChildQuestionRequest> WaitForChildQuestion(
        IChildQuestionCoordinator coordinator,
        IAgentSession parent,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = coordinator.PendingForParent(parent);
            if (pending.Count > 0)
            {
                return pending[0];
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
