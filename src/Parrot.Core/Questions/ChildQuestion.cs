using Parrot.Agent;

namespace Parrot.Questions;

internal sealed class ChildQuestion(AgentIdentity owner) : IChildQuestion
{
    private readonly Lock _gate = new();
    private OpenChildQuestion? _open;

    public OpenChildQuestion Open(IReadOnlyList<QuestionDefinition> questions)
    {
        lock (_gate)
        {
            if (_open is not null)
            {
                throw new QuestionRejectedException("the child already has a pending question");
            }

            _open = new OpenChildQuestion(Identifier.QuestionRequestId(), questions);
            return _open;
        }
    }

    public PendingChildQuestionRequest? Snapshot()
    {
        lock (_gate)
        {
            return _open is { } open
                ? new PendingChildQuestionRequest(
                    open.Id,
                    owner.SessionId,
                    owner.Name,
                    owner.ParentSessionId,
                    QuestionValidation.CopyQuestions(open.Questions))
                : null;
        }
    }

    public void Reply(QuestionReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        lock (_gate)
        {
            var open = _open ?? throw new QuestionRejectedException("child question request is no longer pending");
            var copied = QuestionValidation.CopyReply(reply);
            QuestionValidation.ValidateReply(open.Questions, copied);
            _open = null;
            open.Settle(copied);
        }
    }

    public bool Withdraw(OpenChildQuestion open)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_open, open))
            {
                return false;
            }

            _open = null;
            return true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _open?.Fail(new QuestionRejectedException("question session closed"));
            _open = null;
        }
    }
}
