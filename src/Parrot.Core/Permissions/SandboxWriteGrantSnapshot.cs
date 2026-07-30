namespace Parrot.Permissions;

internal sealed class SandboxWriteGrantSnapshot
{
    internal SandboxWriteGrantSnapshot(IEnumerable<SandboxWriteTarget> targets) =>
        Targets = Array.AsReadOnly(targets.OrderBy(target => target.Path, StringComparer.Ordinal).ToArray());

    public static SandboxWriteGrantSnapshot Empty { get; } = new([]);

    public IReadOnlyList<SandboxWriteTarget> Targets { get; }

    public IReadOnlyList<SandboxWriteTarget> CaptureValid()
    {
        var valid = new List<SandboxWriteTarget>();
        foreach (var target in Targets)
        {
            try
            {
                target.Validate();
                valid.Add(target);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return valid;
    }

    public void Validate(string path)
    {
        foreach (var target in Targets.Where(target => target.Includes(path)))
        {
            target.Validate();
        }
    }
}
