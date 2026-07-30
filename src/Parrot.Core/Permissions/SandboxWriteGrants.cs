namespace Parrot.Permissions;

internal sealed class SandboxWriteGrants
{
    private readonly Lock _gate = new();
    private readonly List<SandboxWriteTarget> _targets = [];

    public void Grant(SandboxWriteTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Grant([target]);
    }

    public void Grant(IReadOnlyList<SandboxWriteTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var copied = targets.ToArray();
        foreach (var target in copied)
        {
            target.Validate();
        }

        lock (_gate)
        {
            foreach (var target in copied)
            {
                if (_targets.Any(existing => existing.Contains(target)))
                {
                    continue;
                }

                _ = _targets.RemoveAll(target.Contains);
                _targets.Add(target);
            }
        }
    }

    public SandboxWriteGrantSnapshot Capture()
    {
        lock (_gate)
        {
            return _targets.Count == 0
                ? SandboxWriteGrantSnapshot.Empty
                : new SandboxWriteGrantSnapshot(_targets);
        }
    }
}
