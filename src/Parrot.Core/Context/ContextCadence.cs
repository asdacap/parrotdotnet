namespace Parrot.Context;

internal sealed class ContextCadence
{
    internal const int NotificationInterval = 5;

    private string? _canonicalModel;
    private int? _contextWindow;
    private long _effectiveHistorySize;
    private int _observedBand;
    private int _reportedBand;
    private bool _suppressNextIncrease;
    private bool _initialized;
    private ContextReminderCheckpoint? _checkpoint;

    internal static int? Band(ContextSnapshot snapshot) => snapshot.UsagePercent is > 0
        ? snapshot.UsagePercent.Value / NotificationInterval
        : null;

    internal void Restore(ContextReminderCheckpoint? checkpoint) => _checkpoint = checkpoint;

    internal int? Observe(
        ContextSnapshot snapshot,
        string canonicalModel,
        long effectiveHistorySize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalModel);
        ArgumentOutOfRangeException.ThrowIfNegative(effectiveHistorySize);

        var band = Band(snapshot) ?? 0;
        if (!_initialized)
        {
            var checkpoint = _checkpoint;
            var matchingCheckpoint = checkpoint is not null
                && string.Equals(checkpoint.CanonicalModel, canonicalModel, StringComparison.Ordinal)
                && checkpoint.ContextLimit == snapshot.ContextLimit;
            var reportedBand = matchingCheckpoint && checkpoint is not null
                ? checkpoint.Percentage / NotificationInterval
                : 0;
            SetState(snapshot, canonicalModel, effectiveHistorySize, band, reportedBand);
            _checkpoint = null;
            return null;
        }

        if (!string.Equals(_canonicalModel, canonicalModel, StringComparison.Ordinal)
            || _contextWindow != snapshot.ContextLimit)
        {
            SetState(snapshot, canonicalModel, effectiveHistorySize, band, 0);
            return null;
        }

        if (effectiveHistorySize < _effectiveHistorySize)
        {
            SetState(snapshot, canonicalModel, effectiveHistorySize, band, band);
            _suppressNextIncrease = true;
            return null;
        }

        _effectiveHistorySize = effectiveHistorySize;
        if (band <= _observedBand)
        {
            return null;
        }

        if (_suppressNextIncrease)
        {
            _suppressNextIncrease = false;
            _observedBand = band;
            return null;
        }

        var crossedBand = band;
        _observedBand = band;
        if (crossedBand <= 0 || crossedBand <= _reportedBand)
        {
            return null;
        }

        _reportedBand = crossedBand;
        return crossedBand * 5;
    }

    internal void Acknowledge(
        ContextSnapshot snapshot,
        string canonicalModel,
        long effectiveHistorySize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalModel);
        ArgumentOutOfRangeException.ThrowIfNegative(effectiveHistorySize);

        var band = Band(snapshot) ?? 0;
        SetState(snapshot, canonicalModel, effectiveHistorySize, band, band);
    }

    internal void Rebase(
        ContextSnapshot snapshot,
        string canonicalModel,
        long effectiveHistorySize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalModel);
        ArgumentOutOfRangeException.ThrowIfNegative(effectiveHistorySize);

        var band = Band(snapshot) ?? 0;
        SetState(snapshot, canonicalModel, effectiveHistorySize, band, band);
    }

    private void SetState(
        ContextSnapshot snapshot,
        string canonicalModel,
        long effectiveHistorySize,
        int observedBand,
        int reportedBand)
    {
        _canonicalModel = canonicalModel;
        _contextWindow = snapshot.ContextLimit;
        _effectiveHistorySize = effectiveHistorySize;
        _observedBand = observedBand;
        _reportedBand = reportedBand;
        _suppressNextIncrease = false;
        _initialized = true;
    }
}
