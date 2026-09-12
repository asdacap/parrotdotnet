using Parrot.Diagnostics;

namespace Parrot.Core.Tests;

internal sealed class TestDiagnosticLog : IDiagnosticLog
{
    public static IDiagnosticLog Instance { get; } = new TestDiagnosticLog();

    public void Write(DiagnosticEvent entry)
    {
    }

    public void Dispose()
    {
    }
}
