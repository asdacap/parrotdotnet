using Parrot.Diagnostics;

namespace Parrot.Llm;

internal sealed class ProviderAttemptDiagnostics(IDiagnosticLog diagnostics, DiagnosticEvent entry)
{
    public long? RequestBytes { get; private set; }

    public long? ResponseBytes { get; private set; }

    public void RecordRequestBytes(int count)
    {
        RequestBytes = count;
        diagnostics.Write(entry with { Operation = "request_size", RequestBytes = count });
    }

    public void MarkResponseObtained() => ResponseBytes ??= 0;

    public void RecordResponseBytes(int count) => ResponseBytes = (ResponseBytes ?? 0) + count;
}
