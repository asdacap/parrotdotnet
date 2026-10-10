using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Questions;

internal sealed class ChildQuestionCoordinator(
    IAgentParentScope ownerScope,
    IChildRegistry children,
    IPromptTemplateCatalog promptTemplates) : IChildQuestionCoordinator
{
    private readonly Lock _gate = new();
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

        var childScope = children.FindNamedChildScope(askingChild.Name);
        if (childScope is null || !ReferenceEquals(childScope.Session, askingChild))
        {
            throw new AgentRegistryException($"child agent not found: {askingChild.Name}");
        }

        var parent = ownerScope.RequireOwnerScope().Session;
        var question = childScope.GetService<IChildQuestion>();
        OpenChildQuestion open;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            open = question.Open(copied);
        }

        try
        {
            _ = await parent.Send(
                [ConversationPart.TextPart(FormatSteer(askingChild.Name, open))],
                open.Id,
                Delivery.Steer,
                new IncomingActivity(string.Empty, null),
                CancellationToken.None).ConfigureAwait(false);
            return await open.Answer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (question.Withdraw(open))
            {
                throw;
            }

            return open.RequireOutcome();
        }
        catch
        {
            _ = question.Withdraw(open);
            throw;
        }
    }

    public IReadOnlyList<PendingChildQuestionRequest> Pending()
    {
        lock (_gate)
        {
            return _disposed ? [] : [.. SnapshotOpenQuestions().OrderBy(item => item.Id, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<PendingChildQuestionRequest> PendingForParent(IAgentSession parent) =>
        string.Equals(parent.SessionId, ownerScope.OwnerSessionId, StringComparison.Ordinal)
            ? Pending()
            : [];

    public string? CompletionReminder()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            var pending = SnapshotOpenQuestions()
                .OrderBy(item => item.AskingAgentName, StringComparer.Ordinal)
                .ToArray();
            return pending.Length > 0 ? FormatCompletionReminder(pending) : null;
        }
    }

    public void Reply(string childName, QuestionReply reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        ArgumentNullException.ThrowIfNull(reply);
        children.ResolveNamedChildScope(childName).GetService<IChildQuestion>().Reply(reply);
    }

    public void ReplyFromParent(IAgentParentScope parentScope, string childName, QuestionReply reply)
    {
        if (!string.Equals(parentScope.OwnerSessionId, ownerScope.OwnerSessionId, StringComparison.Ordinal))
        {
            _ = parentScope.RequireOwnerScope().ChildRegistry.ResolveNamedChildScope(childName);
            throw new QuestionException("only the owning parent may answer a child question");
        }

        Reply(childName, reply);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var child in children.SnapshotChildScopes())
            {
                child.GetService<IChildQuestion>().Close();
            }
        }
    }

    private IEnumerable<PendingChildQuestionRequest> SnapshotOpenQuestions() =>
        children.SnapshotChildScopes()
            .Select(static child => child.GetService<IChildQuestion>().Snapshot())
            .OfType<PendingChildQuestionRequest>();

    private string FormatCompletionReminder(IReadOnlyList<PendingChildQuestionRequest> pending)
    {
        var childNames = new StringBuilder();
        foreach (var request in pending)
        {
            _ = childNames.Append("\n- ").Append(request.AskingAgentName);
        }

        return promptTemplates.Render(
            "agent-session.pending-child-question-reminder",
            [new PromptTemplateArgument("children", childNames.ToString())]);
    }

    private string FormatSteer(string askingAgentName, OpenChildQuestion open)
    {
        var questions = new StringBuilder();
        for (var index = 0; index < open.Questions.Count; index++)
        {
            var question = open.Questions[index];
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
                _ = questions.AppendLine(option.Description.Length == 0
                    ? $"- {option.Label}"
                    : $"- {option.Label} — {option.Description}");
            }
        }

        return promptTemplates.Render(
            "agent-session.child-question",
            [
                new PromptTemplateArgument("agent_name", askingAgentName),
                new PromptTemplateArgument("questions", questions.ToString().TrimEnd()),
            ]);
    }
}
