namespace Parrot.Auth;

// Opens a URL in the user's browser. An interface so the login flow is testable
// headless and never actually launches a browser under test.
internal interface IBrowserOpener
{
    Task Open(string url, CancellationToken cancellationToken);
}
