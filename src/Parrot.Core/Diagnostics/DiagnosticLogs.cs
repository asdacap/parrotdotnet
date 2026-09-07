using Parrot.State;
using Parrot.Store;

namespace Parrot.Diagnostics;

internal sealed class DiagnosticLogs(
    StatePaths paths,
    string instanceId,
    TextWriter error,
    TimeProvider timeProvider) : IDisposable
{
    public IDiagnosticLog Global { get; } = FileDiagnosticLog.OpenGlobal(paths, instanceId, error, timeProvider);

    public IDiagnosticLog OpenSession(UserSessionResources resources) =>
        FileDiagnosticLog.OpenSession(resources, instanceId, error, timeProvider);

    public void Dispose() => Global.Dispose();
}
