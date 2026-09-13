namespace Parrot.Process;

/// <summary>Holds the process-wide sandbox switch that the runtime sandbox toggle mutates.</summary>
internal sealed class SandboxGate(bool enabled)
{
    // A single atomic bool toggle; callers only read it before each process launch.
    public bool Enabled { get; private set; } = enabled;

    public void SetEnabled(bool value) => Enabled = value;
}
