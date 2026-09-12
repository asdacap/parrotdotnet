using System.ComponentModel;
using Parrot.Auth;

namespace Parrot.Core.Tests;

internal sealed class SystemBrowserOpenerTests
{
    [Test]
    public async Task Process_launch_failures_are_actionable_auth_errors(CancellationToken cancellationToken)
    {
        foreach (var failure in new Exception[] { new Win32Exception(2), new InvalidOperationException("invalid") })
        {
            IBrowserOpener opener = new SystemBrowserOpener(_ => throw failure);

            var thrown = await Assert.That(async () => await opener.Open("https://example.com", cancellationToken))
                .Throws<AuthException>();

            _ = await Assert.That(thrown?.Message).Contains("--device");
            _ = await Assert.That(thrown?.InnerException).IsSameReferenceAs(failure);
        }
    }

    [Test]
    public async Task A_missing_process_is_an_actionable_auth_error(CancellationToken cancellationToken)
    {
        IBrowserOpener opener = new SystemBrowserOpener(static _ => null);

        var thrown = await Assert.That(async () => await opener.Open("https://example.com", cancellationToken))
            .Throws<AuthException>();

        _ = await Assert.That(thrown?.Message).Contains("--device");
        _ = await Assert.That(thrown?.InnerException).IsNull();
    }
}
