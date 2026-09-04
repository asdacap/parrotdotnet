using Parrot.Agent;
using Parrot.Llm;
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
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var unrelated = Session(provider, 0, "unrelated", registry, cancellationToken);
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "child")).Session;
        var options = new[] { "Blue" };
        var questions = new[] { new QuestionDefinition("Colour", "Pick", options, false, false) };
        var asking = coordinator.Ask(child, questions, cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        await provider.Arrived(cancellationToken);
        var steer = string.Join('\n', provider.Requests.Single().Messages.Select(message => message.Content));

        _ = await Assert.That(steer).Contains($"Child agent {child.Name} ({child.SessionId})");
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

        options[0] = "Red";
        questions[0] = new QuestionDefinition(string.Empty, "Changed", [], false, true);

        _ = await Assert.That(pending.AskingAgentSessionId).IsEqualTo(child.SessionId);
        _ = await Assert.That(pending.AskingAgentName).IsEqualTo(child.Name);
        _ = await Assert.That(pending.ParentAgentSessionId).IsEqualTo(parent.SessionId);
        _ = await Assert.That(pending.Questions.Single().Options.Single()).IsEqualTo("Blue");
        _ = await Assert.That(coordinator.Pending(unrelated)).IsEmpty();
        _ = await Assert.That(async () =>
            await coordinator.Ask(child, [Question("duplicate")], cancellationToken))
            .Throws<QuestionRejectedException>();
        _ = await Assert.That(() => coordinator.Reply(
            TestModels.ScopeOf(unrelated).ParentScope,
            child.SessionId,
            Answer("blue"))).Throws<AgentRegistryException>();
        _ = await Assert.That(() => coordinator.Reply(
            TestModels.ScopeOf(parent).ParentScope,
            child.SessionId,
            new QuestionReply([new QuestionAnswer(string.Empty)]))).Throws<QuestionException>();
        _ = await Assert.That(coordinator.Pending(parent)).Count().IsEqualTo(1);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(
            () =>
            {
                try
                {
                    coordinator.Reply(TestModels.ScopeOf(parent).ParentScope, child.SessionId, Answer("blue"));
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
        _ = await Assert.That(coordinator.Pending(parent)).IsEmpty();
        _ = await Assert.That(() => coordinator.Reply(
            TestModels.ScopeOf(parent).ParentScope,
            child.SessionId,
            Answer("blue"))).Throws<QuestionRejectedException>();
    }

    [Test]
    public async Task Answer_tool_authorizes_direct_parent_and_invalid_answers_leave_request_pending(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent-tool", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var unrelated = Session(provider, 0, "unrelated-tool", registry, cancellationToken);
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "tool-child")).Session;
        var asking = coordinator.Ask(child, [Question("continue")], cancellationToken);
        _ = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        var arguments = $$"""
            {"agent_session_id":"{{child.SessionId}}","answers":["yes"]}
            """;
        var invalidArguments = $$"""
            {"agent_session_id":"{{child.SessionId}}","answers":[""]}
            """;

        var unauthorized = await new AnswerTool(coordinator, TestModels.ScopeOf(unrelated).ParentScope).Execute(
            new ToolInvocation("unauthorized", arguments),
            Turn(unrelated, router),
            cancellationToken);
        var invalid = await new AnswerTool(coordinator, TestModels.ScopeOf(parent).ParentScope).Execute(
            new ToolInvocation("invalid", invalidArguments),
            Turn(parent, router),
            cancellationToken);

        _ = await Assert.That(unauthorized.Text).StartsWith("error: child agent not found:");
        _ = await Assert.That(invalid.Text).IsEqualTo("error: question answers cannot be empty");
        _ = await Assert.That(coordinator.Pending(parent)).Count().IsEqualTo(1);

        var answered = await new AnswerTool(coordinator, TestModels.ScopeOf(parent).ParentScope).Execute(
            new ToolInvocation("answered", arguments),
            Turn(parent, router),
            cancellationToken);

        _ = await Assert.That(answered.Text).IsEqualTo($"Answered the pending question from child agent {child.SessionId}.");
        _ = await Assert.That((await asking).Answers.Single().Text).IsEqualTo("yes");
        _ = await Assert.That(coordinator.Pending(parent)).IsEmpty();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await parent.DisposeAsync();
    }

    [Test]
    public async Task Cancellation_disposal_and_user_session_ownership_leave_no_child_question_waiter(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var firstRegistry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var secondRegistry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", firstRegistry, cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", secondRegistry, cancellationToken);
        var firstCoordinator = new ChildQuestionCoordinator(TestModels.ScopeOf(firstParent).ParentScope, TestModels.PromptTemplates);
        var secondCoordinator = new ChildQuestionCoordinator(TestModels.ScopeOf(secondParent).ParentScope, TestModels.PromptTemplates);
        var firstChild = TestModels.ScopeOf(firstParent).ChildRegistry.SpawnScope(QuestionChildRequest(firstParent, router, "first-child")).Session;
        var secondChild = TestModels.ScopeOf(secondParent).ChildRegistry.SpawnScope(QuestionChildRequest(secondParent, router, "second-child")).Session;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = firstCoordinator.Ask(firstChild, [Question("cancelled")], stopping.Token);
        _ = await WaitForChildQuestion(firstCoordinator, firstParent, cancellationToken);
        var disposed = firstCoordinator.Ask(
            TestModels.ScopeOf(firstParent).ChildRegistry.SpawnScope(QuestionChildRequest(firstParent, router, "disposed-child")).Session,
            [Question("disposed")],
            cancellationToken);
        var isolated = secondCoordinator.Ask(secondChild, [Question("isolated")], cancellationToken);
        _ = await WaitForChildQuestion(secondCoordinator, secondParent, cancellationToken);

        await stopping.CancelAsync();
        _ = await Assert.That(cancelled).Throws<OperationCanceledException>();
        _ = await Assert.That(firstCoordinator.Pending(firstParent)).Count().IsEqualTo(1);
        _ = await Assert.That(secondCoordinator.Pending(firstParent)).IsEmpty();

        firstCoordinator.Dispose();
        firstCoordinator.Dispose();
        _ = await Assert.That(disposed).Throws<QuestionRejectedException>();
        _ = await Assert.That(firstCoordinator.Pending(firstParent)).IsEmpty();
        _ = await Assert.That(async () =>
            await firstCoordinator.Ask(firstChild, [Question("late")], cancellationToken))
            .Throws<ObjectDisposedException>();

        secondCoordinator.Reply(TestModels.ScopeOf(secondParent).ParentScope, secondChild.SessionId, Answer("yes"));
        _ = await isolated;

        await TestModels.ScopeOf(firstParent).DisposeAsync();
        await TestModels.ScopeOf(secondParent).DisposeAsync();
    }

    [Test]
    [Arguments("not-json")]
    [Arguments("{}")]
    [Arguments("{\"agent_session_id\":\"{0}\"}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[null]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[\"\"]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[\"yes\",\"no\"]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[{\"text\":\"yes\"}]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[\"yes\"],\"extra\":true}")]
    public async Task Malformed_incomplete_duplicate_and_stale_answers_leave_child_request_pending(
        string argumentsTemplate,
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "answer-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "answer-child")).Session;
        var asking = coordinator.Ask(child, [Question("continue")], cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        var arguments = argumentsTemplate.Replace("{0}", child.SessionId, StringComparison.Ordinal);

        var result = await new AnswerTool(coordinator, TestModels.ScopeOf(parent).ParentScope).Execute(
            new ToolInvocation("malformed", arguments),
            Turn(parent, router),
            cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("error:");
        _ = await Assert.That(coordinator.Pending(parent)).HasSingleItem();
        coordinator.Reply(TestModels.ScopeOf(parent).ParentScope, child.SessionId, Answer("yes"));
        _ = await Assert.That((await asking).Answers.Single().Text).IsEqualTo("yes");
        _ = await Assert.That(() => coordinator.Reply(TestModels.ScopeOf(parent).ParentScope, child.SessionId, Answer("yes")))
            .Throws<QuestionRejectedException>();
        _ = await Assert.That(pending.Id).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task Nested_child_questions_use_the_immediate_parent_without_routing_to_the_root(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "question-root", registry, cancellationToken);
        var parent = TestModels.ScopeOf(root).ChildRegistry.SpawnScope(QuestionChildRequest(root, router, "question-parent")).Session;
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "question-child")).Session;

        var asking = coordinator.Ask(child, [Question("nested")], cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(pending.ParentAgentSessionId).IsEqualTo(parent.SessionId);
        _ = await Assert.That(provider.Requests).HasSingleItem();
        _ = await Assert.That(provider.Requests.Single().Messages.Select(message => message.Content))
            .Contains(content => content.Contains(child.SessionId, StringComparison.Ordinal));
        _ = await Assert.That(coordinator.Pending(root)).IsEmpty();

        coordinator.Reply(TestModels.ScopeOf(parent).ParentScope, child.SessionId, Answer("yes"));
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
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "multiple-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var first = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "first-question-child")).Session;
        var second = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "second-question-child")).Session;

        var firstAsking = coordinator.Ask(first, [Question("first")], cancellationToken);
        var secondAsking = coordinator.Ask(second, [Question("second")], cancellationToken);
        _ = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        _ = await Assert.That(coordinator.Pending(parent)).Count().IsEqualTo(2);
        await provider.Arrived(cancellationToken);

        var reminder = coordinator.BeginCompletion(parent);
        _ = await Assert.That(reminder.Reminder).IsNotNull();
        _ = await Assert.That(reminder.Reminder).Contains(first.Name);
        _ = await Assert.That(reminder.Reminder).Contains(second.Name);
        reminder.Dispose();
        coordinator.Reply(TestModels.ScopeOf(parent).ParentScope, first.SessionId, Answer("yes"));
        coordinator.Reply(TestModels.ScopeOf(parent).ParentScope, second.SessionId, Answer("yes"));
        _ = await Task.WhenAll(firstAsking, secondAsking);
        provider.Release();
        await parent.DisposeAsync();
    }

    [Test]
    public async Task Question_factory_keeps_root_questions_in_the_user_broker_and_children_out_of_it(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        using var userQuestions = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System);
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "factory-root", registry, cancellationToken);
        var rootScope = _rootScopes[^1];
        var child = TestModels.ScopeOf(root).ChildRegistry.SpawnScope(QuestionChildRequest(root, router, "factory-child")).Session;
        var childQuestions = rootScope.ChildQuestions;
        var rootFactory = new QuestionToolFactory(userQuestions, AgentSessionParentScope.Root());
        var childFactory = new QuestionToolFactory(userQuestions, AgentSessionParentScope.Child(rootScope, AgentCompletionDeliveryPolicy.RetainedOnly));
        const string request = "{\"questions\":[{\"prompt\":\"Choose\",\"options\":[\"Yes\"]}]}";

        var rootExecution = rootFactory.Create(root).Execute(
            new ToolInvocation("root-question", request),
            Turn(root, router),
            cancellationToken);
        _ = await Assert.That(userQuestions.Pending()).HasSingleItem();
        _ = await Assert.That(childQuestions.Pending(root)).IsEmpty();
        userQuestions.Reply(userQuestions.Pending().Single().Id, new QuestionReply(
            [new QuestionAnswer("yes")]));
        _ = await rootExecution;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var childExecution = childFactory.Create(child).Execute(
            new ToolInvocation("child-question", request),
            Turn(child, router),
            stopping.Token);
        _ = await Assert.That(childQuestions.Pending(root)).HasSingleItem();
        _ = await Assert.That(userQuestions.Pending()).IsEmpty();
        await stopping.CancelAsync();
        _ = await Assert.That(childExecution).Throws<OperationCanceledException>();
        _ = await Assert.That(childQuestions.Pending(root)).IsEmpty();
        provider.Release();
        await root.DisposeAsync();
    }

    [Test]
    public async Task Cancellation_while_waiting_for_parent_completion_reservation_is_deterministic(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "reservation-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(TestModels.ScopeOf(parent));
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "reservation-child")).Session;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reservation = coordinator.BeginCompletion(parent);
        var asking = coordinator.Ask(child, [Question("reserved")], stopping.Token);

        await stopping.CancelAsync();
        _ = await Assert.That(asking).Throws<OperationCanceledException>();
        _ = await Assert.That(coordinator.Pending(parent)).IsEmpty();
    }

    [Test]
    public async Task Direct_child_authorization_rejects_grandchildren_and_cross_edges(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);
        var unrelated = Session(provider, 0, "unrelated", registry, cancellationToken);
        var child = TestModels.ScopeOf(root).ChildRegistry.SpawnScope(QuestionChildRequest(root, router, "child")).Session;
        var grandchild = TestModels.ScopeOf(child).ChildRegistry.SpawnScope(QuestionChildRequest(child, router, "grandchild")).Session;

        _ = await Assert.That(TestModels.ScopeOf(root).ParentScope.AuthorizeDirectChild(child.SessionId)).IsSameReferenceAs(TestModels.ScopeOf(child));
        _ = await Assert.That(() => TestModels.ScopeOf(root).ParentScope.AuthorizeDirectChild(grandchild.SessionId))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(() => TestModels.ScopeOf(unrelated).ParentScope.AuthorizeDirectChild(child.SessionId))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(() => TestModels.ScopeOf(root).ParentScope.AuthorizeDirectChild(child.Name))
            .Throws<AgentRegistryException>();
    }

    private static AgentLaunchRequest QuestionChildRequest(
        IAgentSession parent,
        ModelRouter router,
        string name) => new(
            parent,
            Turn(parent, router),
            "worker",
            parent.Selection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic);

    private static QuestionDefinition Question(string prompt) => new(
        "Continue",
        prompt,
        ["Yes"],
        false,
        false);

    private static QuestionReply Answer(string text) =>
        new([new QuestionAnswer(text)]);

    private static async Task<PendingChildQuestionRequest> WaitForChildQuestion(
        ChildQuestionCoordinator coordinator,
        IAgentSession parent,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = coordinator.Pending(parent);
            if (pending.Count > 0)
            {
                return pending[0];
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
