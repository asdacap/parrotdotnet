using System.ComponentModel;

namespace Parrot.Auth;

// Launches the platform browser. Linux uses xdg-open, macOS uses open; Windows
// is out of scope (MIGRATION.md section 7).
internal sealed class SystemBrowserOpener(Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?> start)
    : IBrowserOpener
{
    public Task Open(string url, CancellationToken cancellationToken)
    {
        var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        try
        {
            using var process = start(new System.Diagnostics.ProcessStartInfo(command, [url]) { UseShellExecute = false })
                ?? throw OpenFailure();
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException)
        {
            throw OpenFailure(failure);
        }

        return Task.CompletedTask;
    }

    private static AuthException OpenFailure(Exception? failure = null) =>
        failure is null
            ? new AuthException("auth: cannot open authorization URL; retry with --device")
            : new AuthException("auth: cannot open authorization URL; retry with --device", failure);
}
