namespace Parrot.Questions;

internal sealed class OpenChildQuestion(string id, IReadOnlyList<QuestionDefinition> questions)
{
    private readonly TaskCompletionSource<QuestionReply> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private QuestionReply? _outcome;
    private QuestionRejectedException? _failure;

    public string Id { get; } = id;

    public IReadOnlyList<QuestionDefinition> Questions { get; } = questions;

    public Task<QuestionReply> Answer => _answer.Task;

    public void Settle(QuestionReply outcome)
    {
        _outcome = outcome;
        _ = _answer.TrySetResult(outcome);
    }

    public void Fail(QuestionRejectedException failure)
    {
        _failure = failure;
        _ = _answer.TrySetException(failure);
    }

    public QuestionReply RequireOutcome() => _failure is not null
        ? throw _failure
        : _outcome ?? throw new InvalidOperationException("The child question has no settled outcome.");
}
