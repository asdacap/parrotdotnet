namespace Parrot.Cli.Enhanced;

internal sealed class RollingTokenRateWindow(TimeProvider timeProvider)
{
    private const int BucketCount = 30;

    private readonly object _synchronization = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly BucketSlot[] _slots = new BucketSlot[BucketCount];
    private readonly long _originTimestamp = timeProvider.GetTimestamp();
    private long _bucket;

    public TokenRate Current
    {
        get
        {
            lock (_synchronization)
            {
                AdvanceCore();
                long inputTokens = 0;
                long outputTokens = 0;
                var earliestBucket = Math.Max(0, _bucket - BucketCount + 1);
                foreach (var slot in _slots)
                {
                    if (slot.Bucket >= earliestBucket && slot.Bucket <= _bucket)
                    {
                        inputTokens = AddNonNegative(inputTokens, slot.InputTokens);
                        outputTokens = AddNonNegative(outputTokens, slot.OutputTokens);
                    }
                }

                return new TokenRate((decimal)inputTokens / BucketCount, (decimal)outputTokens / BucketCount);
            }
        }
    }

    public bool HasRetainedSamples
    {
        get
        {
            lock (_synchronization)
            {
                AdvanceCore();
                return _slots.Any(slot => slot.Bucket >= Math.Max(0, _bucket - BucketCount + 1)
                    && slot.Bucket <= _bucket
                    && (slot.InputTokens > 0 || slot.OutputTokens > 0));
            }
        }
    }

    public void Observe(long inputTokens, long outputTokens)
    {
        lock (_synchronization)
        {
            AdvanceCore();
            var slotIndex = (int)(_bucket % BucketCount);
            ref var slot = ref _slots[slotIndex];
            if (slot.Bucket != _bucket)
            {
                slot = new BucketSlot(_bucket, 0, 0);
            }

            slot.InputTokens = AddNonNegative(slot.InputTokens, inputTokens);
            slot.OutputTokens = AddNonNegative(slot.OutputTokens, outputTokens);
        }
    }

    public TimeSpan GetExpiryDelay()
    {
        lock (_synchronization)
        {
            AdvanceCore();
            var earliestBucket = _slots
                .Where(slot => slot.Bucket >= Math.Max(0, _bucket - BucketCount + 1)
                    && slot.Bucket <= _bucket
                    && (slot.InputTokens > 0 || slot.OutputTokens > 0))
                .Select(slot => slot.Bucket)
                .DefaultIfEmpty(-1)
                .Min();
            if (earliestBucket < 0)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var delay = TimeSpan.FromSeconds(earliestBucket + BucketCount) - Elapsed();
            return delay > TimeSpan.Zero ? delay : TimeSpan.FromTicks(1);
        }
    }

    public void Advance()
    {
        lock (_synchronization)
        {
            AdvanceCore();
        }
    }

    public void Reset()
    {
        lock (_synchronization)
        {
            Array.Clear(_slots);
            _bucket = Math.Max(0, (long)(Elapsed().Ticks / TimeSpan.TicksPerSecond));
        }
    }

    private static long AddNonNegative(long total, long addition) => addition <= 0
        ? total
        : total > long.MaxValue - addition ? long.MaxValue : total + addition;

    private void AdvanceCore()
    {
        var nextBucket = Math.Max(0, (long)(Elapsed().Ticks / TimeSpan.TicksPerSecond));
        if (nextBucket <= _bucket)
        {
            return;
        }

        if (nextBucket - _bucket >= BucketCount)
        {
            Array.Clear(_slots);
        }
        else
        {
            for (var bucket = _bucket + 1; bucket <= nextBucket; bucket++)
            {
                _slots[(int)(bucket % BucketCount)] = default;
            }
        }

        _bucket = nextBucket;
    }

    private TimeSpan Elapsed()
    {
        var elapsed = _timeProvider.GetElapsedTime(_originTimestamp, _timeProvider.GetTimestamp());
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private struct BucketSlot(long bucket, long inputTokens, long outputTokens)
    {
        public long Bucket = bucket;
        public long InputTokens = inputTokens;
        public long OutputTokens = outputTokens;
    }
}
