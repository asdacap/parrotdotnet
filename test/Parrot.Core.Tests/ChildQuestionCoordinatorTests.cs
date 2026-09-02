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
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(registry, parent.SessionId);
        var unrelated = Session(provider, 0, "unrelated", registry, cancellationToken);
        var child = registry.Spawn(QuestionChildRequest(parent, router, "child"));
        var options = new[] { new QuestionOption("blue", "Blue") };
        var questions = new[] { new QuestionDefinition("colour", "Colour", "Pick", options, false, false) };
        var asking = coordinator.Ask(child, questions, cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        await provider.Arrived(cancellationToken);
        var steer = string.Join('\n', provider.Requests.Single().Messages.Select(message => message.Content));

        _ = await Assert.That(steer).Contains($"Child agent {child.Name} ({child.SessionId})");
        _ = await Assert.That(steer).Contains("Question ID: colour");
        _ = await Assert.That(steer).Contains("Header: Colour");
        _ = await Assert.That(steer).Contains("Prompt: Pick");
        _ = await Assert.That(steer).Contains("Multiple: false");
        _ = await Assert.That(steer).Contains("Custom: false");
        _ = await Assert.That(steer).Contains("- blue: Blue");
        _ = await Assert.That(_repository.Replay()).Contains(published =>
            published.AgentSessionId == parent.SessionId
            && published.PayloadCase == ProtocolEvent.PayloadOneofCase.InputAdmitted
            && published.InputAdmitted.Delivery == Delivery.Steer);
        provider.Release();
        _ = await parent.ResultSettled();

        options[0] = new QuestionOption("red", "Red");
        questions[0] = new QuestionDefinition("changed", string.Empty, "Changed", [], false, true);

        _ = await Assert.That(pending.AskingAgentSessionId).IsEqualTo(child.SessionId);
        _ = await Assert.That(pending.AskingAgentName).IsEqualTo(child.Name);
        _ = await Assert.That(pending.ParentAgentSessionId).IsEqualTo(parent.SessionId);
        _ = await Assert.That(pending.Questions.Single().Options.Single().Id).IsEqualTo("blue");
        _ = await Assert.That(coordinator.Pending(unrelated)).IsEmpty();
        _ = await Assert.That(async () =>
            await coordinator.Ask(child, [Question("duplicate")], cancellationToken))
            .Throws<QuestionRejectedException>();
        _ = await Assert.That(() => coordinator.Reply(
            unrelated,
            child.SessionId,
            Answer("colour", "blue"))).Throws<AgentRegistryException>();
        _ = await Assert.That(() => coordinator.Reply(
            parent,
            child.SessionId,
            Answer("colour", "red"))).Throws<QuestionException>();
        _ = await Assert.That(coordinator.Pending(parent)).Count().IsEqualTo(1);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(
            () =>
            {
                try
                {
                    coordinator.Reply(parent, child.SessionId, Answer("colour", "blue"));
                    return true;
                }
                catch (QuestionRejectedException)
                {
                    return false;
                }
            },
            cancellationToken)));

        _ = await Assert.That(attempts.Count(static succeeded => succeeded)).IsEqualTo(1);
        _ = await Assert.That((await asking).Answers.Single().OptionIds.Single()).IsEqualTo("blue");
        _ = await Assert.That(coordinator.Pending(parent)).IsEmpty();
        _ = await Assert.That(() => coordinator.Reply(
            parent,
            child.SessionId,
            Answer("colour", "blue"))).Throws<QuestionRejectedException>();
    }

    [Test]
    public async Task Answer_tool_authorizes_direct_parent_and_invalid_answers_leave_request_pending(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var parent = Session(provider, 0, "parent-tool", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(registry, parent.SessionId);
        var unrelated = Session(provider, 0, "unrelated-tool", registry, cancellationToken);
        var child = registry.Spawn(QuestionChildRequest(parent, router, "tool-child"));
        var asking = coordinator.Ask(child, [Question("continue")], cancellationToken);
        _ = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        var arguments = $$"""
            {"agent_session_id":"{{child.SessionId}}","answers":[{"question_id":"continue","option_ids":["yes"],"custom":""}]}
            """;
        var invalidArguments = $$"""
            {"agent_session_id":"{{child.SessionId}}","answers":[{"question_id":"continue","option_ids":["unknown"],"custom":""}]}
            """;

        var unauthorized = await new AnswerTool(coordinator, unrelated).Execute(
            new ToolInvocation("unauthorized", arguments),
            Turn(unrelated, router),
            cancellationToken);
        var invalid = await new AnswerTool(coordinator, parent).Execute(
            new ToolInvocation("invalid", invalidArguments),
            Turn(parent, router),
            cancellationToken);

        _ = await Assert.That(unauthorized.Text).StartsWith("error: child agent not found:");
        _ = await Assert.That(invalid.Text).IsEqualTo("error: unknown option id: unknown");
        _ = await Assert.That(coordinator.Pending(parent)).Count().IsEqualTo(1);

        var answered = await new AnswerTool(coordinator, parent).Execute(
            new ToolInvocation("answered", arguments),
            Turn(parent, router),
            cancellationToken);

        _ = await Assert.That(answered.Text).IsEqualTo($"Answered the pending question from child agent {child.SessionId}.");
        _ = await Assert.That((await asking).Answers.Single().OptionIds.Single()).IsEqualTo("yes");
        _ = await Assert.That(coordinator.Pending(parent)).IsEmpty();
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await parent.ResultSettled();
    }

    [Test]
    public async Task Cancellation_disposal_and_user_session_ownership_leave_no_child_question_waiter(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var firstRegistry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var secondRegistry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", firstRegistry, cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", secondRegistry, cancellationToken);
        var firstCoordinator = new ChildQuestionCoordinator(firstParent.SessionId, firstRegistry, TestModels.PromptTemplates);
        var secondCoordinator = new ChildQuestionCoordinator(secondParent.SessionId, secondRegistry, TestModels.PromptTemplates);
        var firstChild = firstRegistry.Spawn(QuestionChildRequest(firstParent, router, "first-child"));
        var secondChild = secondRegistry.Spawn(QuestionChildRequest(secondParent, router, "second-child"));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = firstCoordinator.Ask(firstChild, [Question("cancelled")], stopping.Token);
        _ = await WaitForChildQuestion(firstCoordinator, firstParent, cancellationToken);
        var disposed = firstCoordinator.Ask(
            firstRegistry.Spawn(QuestionChildRequest(firstParent, router, "disposed-child")),
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

        secondCoordinator.Reply(secondParent, secondChild.SessionId, Answer("isolated", "yes"));
        _ = await isolated;
    }

    [Test]
    [Arguments("not-json")]
    [Arguments("{}")]
    [Arguments("{\"agent_session_id\":\"{0}\"}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[{\"question_id\":\"missing\",\"option_ids\":[\"yes\"],\"custom\":\"\"}]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[{\"question_id\":\"continue\",\"option_ids\":[\"yes\",\"yes\"],\"custom\":\"\"}]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[{\"question_id\":\"continue\",\"option_ids\":[\"yes\"],\"custom\":\"\"},{\"question_id\":\"continue\",\"option_ids\":[\"yes\"],\"custom\":\"\"}]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[{\"question_id\":\"continue\",\"option_ids\":[\"yes\"],\"custom\":\"free text\"}]}")]
    [Arguments("{\"agent_session_id\":\"{0}\",\"answers\":[{\"question_id\":\"continue\",\"option_ids\":[\"yes\"],\"custom\":\"\",\"extra\":true}]}")]
    public async Task Malformed_incomplete_duplicate_and_stale_answers_leave_child_request_pending(
        string argumentsTemplate,
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var parent = Session(provider, 0, "answer-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(registry, parent.SessionId);
        var child = registry.Spawn(QuestionChildRequest(parent, router, "answer-child"));
        var asking = coordinator.Ask(child, [Question("continue")], cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        var arguments = argumentsTemplate.Replace("{0}", child.SessionId, StringComparison.Ordinal);

        var result = await new AnswerTool(coordinator, parent).Execute(
            new ToolInvocation("malformed", arguments),
            Turn(parent, router),
            cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("error:");
        _ = await Assert.That(coordinator.Pending(parent)).HasSingleItem();
        coordinator.Reply(parent, child.SessionId, Answer("continue", "yes"));
        _ = await Assert.That((await asking).Answers.Single().OptionIds).HasSingleItem();
        _ = await Assert.That(() => coordinator.Reply(parent, child.SessionId, Answer("continue", "yes")))
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
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var root = Session(provider, 0, "question-root", registry, cancellationToken);
        var parent = registry.Spawn(QuestionChildRequest(root, router, "question-parent"));
        var coordinator = TestModels.CreateChildQuestions(registry, parent.SessionId);
        var child = registry.Spawn(QuestionChildRequest(parent, router, "question-child"));

        var asking = coordinator.Ask(child, [Question("nested")], cancellationToken);
        var pending = await WaitForChildQuestion(coordinator, parent, cancellationToken);
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(pending.ParentAgentSessionId).IsEqualTo(parent.SessionId);
        _ = await Assert.That(provider.Requests).HasSingleItem();
        _ = await Assert.That(provider.Requests.Single().Messages.Select(message => message.Content))
            .Contains(content => content.Contains(child.SessionId, StringComparison.Ordinal));
        _ = await Assert.That(coordinator.Pending(root)).IsEmpty();

        coordinator.Reply(parent, child.SessionId, Answer("nested", "yes"));
        provider.Release();
        _ = await Assert.That((await asking).Answers.Single().OptionIds).HasSingleItem();
        _ = await parent.ResultSettled();
        _ = await Assert.That(provider.Requests).HasSingleItem();
    }

    [Test]
    public async Task Multiple_children_are_pending_independently_and_are_all_listed_in_completion_reminder(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var parent = Session(provider, 0, "multiple-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(registry, parent.SessionId);
        var first = registry.Spawn(QuestionChildRequest(parent, router, "first-question-child"));
        var second = registry.Spawn(QuestionChildRequest(parent, router, "second-question-child"));

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
        coordinator.Reply(parent, first.SessionId, Answer("first", "yes"));
        coordinator.Reply(parent, second.SessionId, Answer("second", "yes"));
        _ = await Task.WhenAll(firstAsking, secondAsking);
        provider.Release();
        _ = await parent.ResultSettled();
    }

    [Test]
    public async Task Question_factory_keeps_root_questions_in_the_user_broker_and_children_out_of_it(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        using var userQuestions = new QuestionBroker(Timeout.InfiniteTimeSpan, TimeProvider.System);
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var root = Session(provider, 0, "factory-root", registry, cancellationToken);
        var rootScope = _rootScopes[^1];
        var child = registry.Spawn(QuestionChildRequest(root, router, "factory-child"));
        var childQuestions = rootScope.ChildQuestions;
        var rootFactory = new QuestionToolFactory(userQuestions, AgentSessionParentScope.Root());
        var childFactory = new QuestionToolFactory(userQuestions, AgentSessionParentScope.Child(rootScope));
        const string request = "{\"questions\":[{\"id\":\"choice\",\"prompt\":\"Choose\",\"options\":[{\"id\":\"yes\",\"label\":\"Yes\"}]}]}";

        var rootExecution = rootFactory.Create(root).Execute(
            new ToolInvocation("root-question", request),
            Turn(root, router),
            cancellationToken);
        _ = await Assert.That(userQuestions.Pending()).HasSingleItem();
        _ = await Assert.That(childQuestions.Pending(root)).IsEmpty();
        userQuestions.Reply(userQuestions.Pending().Single().Id, new QuestionReply(
            [new QuestionAnswer("choice", ["yes"], string.Empty)]));
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
        _ = await root.ResultSettled();
    }

    [Test]
    public async Task Cancellation_while_waiting_for_parent_completion_reservation_is_deterministic(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var router = Router(provider);
        await using var registry = TestModels.Registry(
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var parent = Session(provider, 0, "reservation-parent", registry, cancellationToken);
        var coordinator = TestModels.CreateChildQuestions(registry, parent.SessionId);
        var child = registry.Spawn(QuestionChildRequest(parent, router, "reservation-child"));
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
            new TestAgentSessions(router, deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var root = Session(provider, 0, "root", registry, cancellationToken);
        var unrelated = Session(provider, 0, "unrelated", registry, cancellationToken);
        var child = registry.Spawn(QuestionChildRequest(root, router, "child"));
        var grandchild = registry.Spawn(QuestionChildRequest(child, router, "grandchild"));

        _ = await Assert.That(registry.AuthorizeDirectChild(root.SessionId, child.SessionId)).IsSameReferenceAs(child);
        _ = await Assert.That(() => registry.AuthorizeDirectChild(root.SessionId, grandchild.SessionId))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(() => registry.AuthorizeDirectChild(unrelated.SessionId, child.SessionId))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(() => registry.AuthorizeDirectChild(root.SessionId, child.Name))
            .Throws<AgentRegistryException>();
    }

    private static AgentLaunchRequest QuestionChildRequest(
        AgentSession parent,
        ModelRouter router,
        string name) => new(
            parent,
            Turn(parent, router),
            "worker",
            parent.Selection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            0,
            string.Empty,
            AgentCompletionDeliveryPolicy.Automatic);

    private static QuestionDefinition Question(string id) => new(
        id,
        string.Empty,
        "Continue?",
        [new QuestionOption("yes", "Yes")],
        false,
        false);

    private static QuestionReply Answer(string questionId, string optionId) =>
        new([new QuestionAnswer(questionId, [optionId], string.Empty)]);

    private static async Task<PendingChildQuestionRequest> WaitForChildQuestion(
        ChildQuestionCoordinator coordinator,
        AgentSession parent,
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
