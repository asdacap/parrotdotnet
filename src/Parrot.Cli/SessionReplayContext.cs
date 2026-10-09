using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal sealed class SessionReplayContext(bool replaying)
{
    private readonly AgentSessionHierarchy _hierarchy = new();
    private Action<bool>? _complete;
    private bool _busy;

    public bool IsReplaying { get; private set; } = replaying;

    public void SetCompletion(Action<bool> complete) => _complete = complete;

    public void Observe(Event published)
    {
        if (!IsReplaying)
        {
            return;
        }

        if (published.PayloadCase == Event.PayloadOneofCase.SessionUsageSnapshot)
        {
            IsReplaying = false;
            _complete?.Invoke(_busy);
            return;
        }

        _hierarchy.Observe(published);
        if (!_hierarchy.IsRoot(published.AgentSessionId))
        {
            return;
        }

        if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted)
        {
            _busy = true;
        }
        else if (published.PayloadCase is Event.PayloadOneofCase.TurnEnded
                 or Event.PayloadOneofCase.ModeTurnCompleted or Event.PayloadOneofCase.TurnFailed)
        {
            _busy = false;
        }
    }
}
