using System.Diagnostics;
using Parrot.Diagnostics;

namespace Parrot.Questions;

internal sealed class QuestionBroker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly IDiagnosticLog _diagnostics;
    private bool _disposed;

    public QuestionBroker(TimeSpan timeout, TimeProvider timeProvider, IDiagnosticLog diagnostics)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The question request timeout must be positive or infinite.");
        }

        _diagnostics = diagnostics;
        _timeout = timeout;
        _timeProvider = timeProvider;
    }

    public async Task<QuestionReply> Ask(IReadOnlyList<QuestionDefinition> questions, CancellationToken cancellationToken)
    {
        var id = Identifier.QuestionRequestId();
        var started = Stopwatch.GetTimestamp();
        _diagnostics.Write(new DiagnosticEvent("question", "wait_started", DiagnosticSeverity.Information) { CorrelationId = id });
        try
        {
            var reply = await WaitForReply(questions, id, cancellationToken).ConfigureAwait(false);
            _diagnostics.Write(new DiagnosticEvent("question", "wait_completed", DiagnosticSeverity.Information)
            {
                CorrelationId = id,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = reply.Kind == QuestionReplyKind.UserAway ? "timeout" : "answered",
            });
            return reply;
        }
        catch (Exception failure)
        {
            _diagnostics.Write(new DiagnosticEvent("question", "wait_completed", failure is OperationCanceledException or QuestionRejectedException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = id,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = failure is OperationCanceledException ? "cancelled"
                    : failure is QuestionRejectedException ? "rejected" : "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    public IReadOnlyList<PendingQuestionRequest> Pending()
    {
        lock (_gate)
        {
            return [.. _pending
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item =>
                {
                    var remaining = _timeout - _timeProvider.GetElapsedTime(item.Value.StartedTimestamp);
                    long? remainingMilliseconds = _timeout == Timeout.InfiniteTimeSpan
                        ? null : (long)Math.Ceiling(Math.Max(0, remaining.TotalMilliseconds));
                    return new PendingQuestionRequest(
                        item.Key, QuestionValidation.CopyQuestions(item.Value.Questions), remainingMilliseconds);
                })];
        }
    }

    public void Reply(string requestId, QuestionReply reply)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ArgumentNullException.ThrowIfNull(reply);

        PendingRequest pending;
        QuestionReply copied;
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var found))
            {
                throw new QuestionException($"question request not found: {requestId}");
            }

            pending = found;
            copied = QuestionValidation.CopyReply(reply);
            QuestionValidation.ValidateReply(pending.Questions, copied);
            pending.PublishOutcome(copied);
            _ = _pending.Remove(requestId);
        }

        pending.Complete();
    }

    public void Reject(string requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);

        PendingRequest pending;
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var found))
            {
                throw new QuestionException($"question request not found: {requestId}");
            }

            pending = found;
            pending.PublishFailure(new QuestionRejectedException("question request rejected"));
            _ = _pending.Remove(requestId);
        }

        pending.Complete();
    }

    public void Dispose()
    {
        PendingRequest[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _pending.Values];
            foreach (var item in pending)
            {
                item.PublishFailure(new QuestionRejectedException("question session closed"));
            }

            _pending.Clear();
        }

        foreach (var item in pending)
        {
            item.Complete();
        }
    }

    private async Task<QuestionReply> WaitForReply(
        IReadOnlyList<QuestionDefinition> questions, string id, CancellationToken cancellationToken)
    {
        var copied = QuestionValidation.CopyQuestions(questions);
        QuestionValidation.ValidateQuestions(copied);
        PendingRequest pending;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pending = new PendingRequest(copied, _timeProvider.GetTimestamp());
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

    private bool Remove(string id, PendingRequest expected)
    {
        lock (_gate)
        {
            return _pending.TryGetValue(id, out var found) && ReferenceEquals(found, expected) && _pending.Remove(id);
        }
    }

    private sealed class PendingRequest(IReadOnlyList<QuestionDefinition> questions, long startedTimestamp)
    {
        private QuestionRejectedException? _failure;
        private QuestionReply? _outcome;

        public IReadOnlyList<QuestionDefinition> Questions { get; } = questions;

        public long StartedTimestamp { get; } = startedTimestamp;

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
