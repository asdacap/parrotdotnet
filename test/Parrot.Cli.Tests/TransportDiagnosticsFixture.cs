using Parrot.Diagnostics;
using Parrot.State;

namespace Parrot.Cli.Tests;

internal sealed class TransportDiagnosticsFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "transport-logs", Guid.NewGuid().ToString("N"));
    private readonly StatePaths _paths;

    public TransportDiagnosticsFixture()
    {
        _paths = new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        Log = FileDiagnosticLog.OpenGlobal(_paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
    }

    public IDiagnosticLog Log { get; }

    public string Read() => File.ReadAllText(Directory.GetFiles(_paths.LogDirectory, "*.log").Single());

    public void Dispose()
    {
        Log.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
