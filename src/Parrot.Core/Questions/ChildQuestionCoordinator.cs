using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Questions;

internal sealed class ChildQuestionCoordinator(
    IAgentParentScope ownerScope,
    IPromptTemplateCatalog promptTemplates) : IChildQuestionCoordinator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private TaskCompletionSource? _completionReservation;
    private bool _disposed;

    public async Task<QuestionReply> Ask(IAgentSession askingChild, IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(askingChild);
        ArgumentNullException.ThrowIfNull(questions);
        cancellationToken.ThrowIfCancellationRequested();
        var copied = QuestionValidation.CopyQuestions(questions);
        QuestionValidation.ValidateQuestions(copied);
        if (askingChild.ParentSessionId.Length == 0)
        {
            throw new QuestionException("only child agents may ask questions");
        }

        _ = ownerScope.AuthorizeDirectChild(askingChild.SessionId);
        var parent = ownerScope.RequireOwnerScope().Session;

        var pending = new PendingRequest(Identifier.QuestionRequestId(), askingChild, ownerScope.OwnerSessionId, copied);

        while (true)
        {
            Task? completion = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pending.ContainsKey(askingChild.SessionId))
                {
                    throw new QuestionRejectedException("the child already has a pending question");
                }

                if (_completionReservation is not null)
                {
                    completion = _completionReservation.Task;
                }
                else
                {
                    _pending.Add(askingChild.SessionId, pending);
                }
            }

            if (completion is null)
            {
                break;
            }

            await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = await parent.Send(
                [ConversationPart.TextPart(FormatSteer(pending))],
                pending.Id,
                Delivery.Steer,
                new IncomingActivity(IncomingActivityKind.Input, string.Empty),
                CancellationToken.None).ConfigureAwait(false);
            return await pending.Answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (Remove(pending))
            {
                throw;
            }

            return pending.RequireOutcome();
        }
        catch
        {
            _ = Remove(pending);
            throw;
        }
    }

    public IReadOnlyList<PendingChildQuestionRequest> Pending()
    {
        lock (_gate)
        {
            return [.. _pending.Values
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.Snapshot())];
        }
    }

    public IReadOnlyList<PendingChildQuestionRequest> PendingForParent(IAgentSession parent) =>
        string.Equals(parent.SessionId, ownerScope.OwnerSessionId, StringComparison.Ordinal)
            ? Pending()
            : [];

    public ChildQuestionCompletionAttempt BeginCompletion()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ChildQuestionCompletionAttempt.Reserve(static () => { });
            }

            var pending = _pending.Values
                .OrderBy(item => item.AskingAgentSessionId, StringComparer.Ordinal)
                .ToArray();
            if (pending.Length > 0)
            {
                return ChildQuestionCompletionAttempt.Block(FormatCompletionReminder(pending));
            }

            if (_completionReservation is not null)
            {
                throw new InvalidOperationException("the parent already owns a completion reservation");
            }

            _completionReservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return ChildQuestionCompletionAttempt.Reserve(ReleaseCompletion);
        }
    }

    public ChildQuestionCompletionAttempt BeginParentCompletion(IAgentSession parent)
    {
        if (!string.Equals(parent.SessionId, ownerScope.OwnerSessionId, StringComparison.Ordinal))
        {
            throw new QuestionException("only the owning parent may complete child questions");
        }

        return BeginCompletion();
    }

    public void Reply(string childSessionId, QuestionReply reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        ArgumentNullException.ThrowIfNull(reply);
        _ = ownerScope.AuthorizeDirectChild(childSessionId);

        PendingRequest pending;
        QuestionReply copied;
        lock (_gate)
        {
            if (!_pending.TryGetValue(childSessionId, out var found))
            {
                throw new QuestionRejectedException("child question request is no longer pending");
            }

            pending = found;
            copied = QuestionValidation.CopyReply(reply);
            QuestionValidation.ValidateReply(pending.Questions, copied);
            pending.PublishOutcome(copied);
            _ = _pending.Remove(childSessionId);
        }

        pending.Complete();
    }

    public void ReplyFromParent(IAgentParentScope parentScope, string childSessionId, QuestionReply reply)
    {
        if (!string.Equals(parentScope.OwnerSessionId, ownerScope.OwnerSessionId, StringComparison.Ordinal))
        {
            _ = parentScope.AuthorizeDirectChild(childSessionId);
            throw new QuestionException("only the owning parent may answer a child question");
        }

        Reply(childSessionId, reply);
    }

    public void Dispose()
    {
        PendingRequest[] pending;
        TaskCompletionSource? reservation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _pending.Values];
            reservation = _completionReservation;
            foreach (var item in pending)
            {
                item.PublishFailure(new QuestionRejectedException("question session closed"));
            }

            _pending.Clear();
            _completionReservation = null;
        }

        foreach (var item in pending)
        {
            item.Complete();
        }

        _ = reservation?.TrySetResult();
    }

    private string FormatCompletionReminder(IReadOnlyList<PendingRequest> pending)
    {
        var children = new StringBuilder();
        foreach (var request in pending)
        {
            _ = children.Append("\n- ")
                .Append(request.AskingAgentName)
                .Append(" (")
                .Append(request.AskingAgentSessionId)
                .Append(')');
        }

        return promptTemplates.Render(
            "agent-session.pending-child-question-reminder",
            [new PromptTemplateArgument("children", children.ToString())]);
    }

    private void ReleaseCompletion()
    {
        TaskCompletionSource? reservation;
        lock (_gate)
        {
            reservation = _completionReservation;
            _completionReservation = null;
        }

        _ = reservation?.TrySetResult();
    }

    private string FormatSteer(PendingRequest pending)
    {
        var questions = new StringBuilder();
        for (var index = 0; index < pending.Questions.Count; index++)
        {
            var question = pending.Questions[index];
            if (index > 0)
            {
                _ = questions.AppendLine();
            }

            _ = questions.AppendLine($"Question {index + 1}:");
            _ = questions.AppendLine($"Header: {question.Header}");
            _ = questions.AppendLine($"Prompt: {question.Prompt}");
            _ = questions.AppendLine($"Multiple: {question.Multiple.ToString().ToLowerInvariant()}");
            _ = questions.AppendLine($"Custom: {question.Custom.ToString().ToLowerInvariant()}");
            _ = questions.AppendLine("Options:");
            foreach (var option in question.Options)
            {
                _ = questions.AppendLine($"- {option}");
            }
        }

        return promptTemplates.Render(
            "agent-session.child-question",
            [
                new PromptTemplateArgument("agent_name", pending.AskingAgentName),
                new PromptTemplateArgument("agent_session_id", pending.AskingAgentSessionId),
                new PromptTemplateArgument("questions", questions.ToString().TrimEnd()),
            ]);
    }

    private bool Remove(PendingRequest expected)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(expected.AskingAgentSessionId, out var found)
                && ReferenceEquals(found, expected)
                && _pending.Remove(expected.AskingAgentSessionId);
        }
    }

    private sealed class PendingRequest(
        string id,
        IAgentSession askingAgent,
        string parentAgentSessionId,
        IReadOnlyList<QuestionDefinition> questions)
    {
        private QuestionRejectedException? _failure;
        private QuestionReply? _outcome;

        public string Id { get; } = id;

        public string AskingAgentSessionId => askingAgent.SessionId;

        public string AskingAgentName => askingAgent.Name;

        public string ParentAgentSessionId { get; } = parentAgentSessionId;

        public IReadOnlyList<QuestionDefinition> Questions { get; } = questions;

        public TaskCompletionSource<QuestionReply> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingChildQuestionRequest Snapshot() => new(
            Id,
            AskingAgentSessionId,
            AskingAgentName,
            ParentAgentSessionId,
            QuestionValidation.CopyQuestions(Questions));

        public QuestionReply RequireOutcome() => _failure is not null
            ? throw _failure
            : _outcome ?? throw new InvalidOperationException("The child question has no settled outcome.");

        public void PublishOutcome(QuestionReply outcome) => _outcome = outcome;

        public void PublishFailure(QuestionRejectedException failure) => _failure = failure;

        public void Complete()
        {
            if (_failure is not null)
            {
                _ = Answer.TrySetException(_failure);
            }
            else
            {
                _ = Answer.TrySetResult(RequireOutcome());
            }
        }
    }
}
