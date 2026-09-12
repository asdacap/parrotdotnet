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
                ?? throw new AuthException("auth: cannot open authorization URL; retry with --device");
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException)
        {
            throw new AuthException("auth: cannot open authorization URL; retry with --device", failure);
        }

        return Task.CompletedTask;
    }
}
