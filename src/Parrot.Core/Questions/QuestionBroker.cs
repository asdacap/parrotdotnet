namespace Parrot.Questions;

internal sealed class QuestionBroker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private bool _disposed;

    public QuestionBroker(TimeSpan timeout, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The question request timeout must be positive or infinite.");
        }

        _timeout = timeout;
        _timeProvider = timeProvider;
    }

    public async Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken)
    {
        var copied = CopyQuestions(questions);
        ValidateQuestions(copied);
        var id = Identifier.QuestionRequestId();
        var pending = new PendingRequest(copied);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Add(id, pending);
        }

        try
        {
            return await pending.Answer.Task.WaitAsync(_timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (Remove(id, pending))
            {
                return QuestionReply.UserAway;
            }

            return pending.RequireOutcome();
        }
        catch (OperationCanceledException)
        {
            if (Remove(id, pending))
            {
                throw;
            }

            return pending.RequireOutcome();
        }
    }

    public IReadOnlyList<PendingQuestionRequest> Pending()
    {
        lock (_gate)
        {
            return [.. _pending
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PendingQuestionRequest(item.Key, CopyQuestions(item.Value.Questions)))];
        }
    }

    public void Reply(string requestId, QuestionReply reply)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(reply);

        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var pending))
            {
                throw new QuestionException($"question request not found: {requestId}");
            }

            var copied = CopyReply(reply);
            ValidateReply(pending.Questions, copied);
            _ = _pending.Remove(requestId);
            pending.Settle(copied);
        }
    }

    public void Reject(string requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var pending))
            {
                throw new QuestionException($"question request not found: {requestId}");
            }

            _ = _pending.Remove(requestId);
            pending.Fail(new QuestionRejectedException("question request rejected"));
        }
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
            var pending = _pending.Values.ToArray();
            _pending.Clear();
            foreach (var item in pending)
            {
                item.Fail(new QuestionRejectedException("question session closed"));
            }
        }
    }

    private static IReadOnlyList<QuestionDefinition> CopyQuestions(IReadOnlyList<QuestionDefinition> questions) =>
        [.. questions.Select(question => new QuestionDefinition(
            question.Id,
            question.Header,
            question.Prompt,
            [.. question.Options.Select(option => new QuestionOption(option.Id, option.Label))],
            question.Multiple,
            question.Custom))];

    private static QuestionReply CopyReply(QuestionReply reply) =>
        new(
            reply.Kind,
            [.. reply.Answers.Select(answer => new QuestionAnswer(answer.QuestionId, [.. answer.OptionIds], answer.Custom))]);

    private static void ValidateQuestions(IReadOnlyList<QuestionDefinition> questions)
    {
        if (questions.Count is < 1 or > 32)
        {
            throw new QuestionException("question requests require between 1 and 32 questions");
        }

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (string.IsNullOrWhiteSpace(question.Id))
            {
                throw new QuestionException("question ids cannot be empty");
            }

            if (!questionIds.Add(question.Id))
            {
                throw new QuestionException($"duplicate question id: {question.Id}");
            }

            if (string.IsNullOrWhiteSpace(question.Prompt))
            {
                throw new QuestionException($"question prompt cannot be empty: {question.Id}");
            }

            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in question.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.Label))
                {
                    throw new QuestionException($"question options require id and label: {question.Id}");
                }

                if (!optionIds.Add(option.Id))
                {
                    throw new QuestionException($"duplicate option id: {option.Id}");
                }
            }

            if (question.Options.Count == 0 && !question.Custom)
            {
                throw new QuestionException($"question requires options or custom answers: {question.Id}");
            }
        }
    }

    private static void ValidateReply(IReadOnlyList<QuestionDefinition> questions, QuestionReply reply)
    {
        if (reply.Kind != QuestionReplyKind.Answered)
        {
            throw new QuestionException("question replies require user answers");
        }

        if (reply.Answers.Count != questions.Count)
        {
            throw new QuestionException("question replies must answer every question exactly once");
        }

        var definitions = questions.ToDictionary(question => question.Id, StringComparer.Ordinal);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var answer in reply.Answers)
        {
            if (!definitions.TryGetValue(answer.QuestionId, out var question))
            {
                throw new QuestionException($"unknown question id: {answer.QuestionId}");
            }

            if (!answered.Add(answer.QuestionId))
            {
                throw new QuestionException($"duplicate answer for question: {answer.QuestionId}");
            }

            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var optionId in answer.OptionIds)
            {
                if (!selected.Add(optionId))
                {
                    throw new QuestionException($"duplicate option answer: {optionId}");
                }

                if (!question.Options.Any(option => string.Equals(option.Id, optionId, StringComparison.Ordinal)))
                {
                    throw new QuestionException($"unknown option id: {optionId}");
                }
            }

            if (!question.Multiple && answer.OptionIds.Count > 1)
            {
                throw new QuestionException($"question does not allow multiple answers: {question.Id}");
            }

            if (answer.Custom.Length > 0 && !question.Custom)
            {
                throw new QuestionException($"question does not allow a custom answer: {question.Id}");
            }

            if (answer.OptionIds.Count == 0 && string.IsNullOrWhiteSpace(answer.Custom))
            {
                throw new QuestionException($"question answer cannot be empty: {question.Id}");
            }

            if (!question.Multiple && answer.OptionIds.Count > 0 && answer.Custom.Length > 0)
            {
                throw new QuestionException($"question does not allow multiple answers: {question.Id}");
            }
        }
    }

    private bool Remove(string id, PendingRequest expected)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out var found) && ReferenceEquals(found, expected) && _pending.Remove(id);
        }
    }

    private sealed class PendingRequest(IReadOnlyList<QuestionDefinition> questions)
    {
        private QuestionRejectedException? _failure;
        private QuestionReply? _outcome;

        public IReadOnlyList<QuestionDefinition> Questions { get; } = questions;

        public TaskCompletionSource<QuestionReply> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public QuestionReply RequireOutcome()
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            return _outcome ?? throw new InvalidOperationException("The question request has no settled outcome.");
        }

        public void Settle(QuestionReply outcome)
        {
            _outcome = outcome;
            _ = Answer.TrySetResult(outcome);
        }

        public void Fail(QuestionRejectedException failure)
        {
            _failure = failure;
            _ = Answer.TrySetException(failure);
        }
    }
}
