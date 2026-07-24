namespace Parrot.Auth;

// Launches the platform browser. Linux uses xdg-open, macOS uses open; Windows
// is out of scope (MIGRATION.md section 7).
internal sealed class SystemBrowserOpener : IBrowserOpener
{
    public Task Open(string url, CancellationToken cancellationToken)
    {
        var command = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(command, [url]) { UseShellExecute = false })
            ?? throw new AuthException("auth: open authorization URL");

        return Task.CompletedTask;
    }
}
