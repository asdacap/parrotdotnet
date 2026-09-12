using Parrot.Diagnostics;

namespace Parrot.Llm;

// Writes the most recent provider request to a single file, replacing the
// previous contents atomically, so the agent state directory always holds the
// last request for debugging. Dump failures never escape.
internal sealed class LastRequestDumper(string path, IDiagnosticLog diagnostics)
{
    private readonly Lock _gate = new();

    public void Dump(byte[] body)
    {
        var tempPath = path + ".tmp";
        try
        {
            lock (_gate)
            {
                try
                {
                    File.WriteAllBytes(tempPath, body);
                    File.Move(tempPath, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            diagnostics.Write(new DiagnosticEvent("provider", "last_request_dump_failed", DiagnosticSeverity.Error)
            {
                ErrorCode = DiagnosticEvent.ClassifyFailure(exception),
            });
        }
    }
}
