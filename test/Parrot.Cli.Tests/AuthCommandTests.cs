using Parrot.Auth;
using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class AuthCommandTests
{
    [Test]
    public async Task Api_key_login_list_and_logout_follow_the_wizard_without_leaking_the_secret(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"parrot-credentials-{Guid.NewGuid():N}.json");
        try
        {
            using var credentials = new FileCredentialStore(path);
            using var http = new HttpClient();
            var oauth = new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions());
            var login = new TestSlashDialog().Select("login", "provider").Secret("  private-key  ");
            var list = new TestSlashDialog().Select("list");
            var logout = new TestSlashDialog().Select("logout", "provider");
            var command = new AuthCommand(credentials, oauth, ["provider"], login);

            await command.Run(string.Empty, cancellationToken);
            _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsNotNull();
            await new AuthCommand(credentials, oauth, ["provider"], list).Run(string.Empty, cancellationToken);
            await new AuthCommand(credentials, oauth, ["provider"], logout).Run(string.Empty, cancellationToken);

            _ = await Assert.That(string.Join('|', login.Shown)).Contains("stored a credential for provider");
            _ = await Assert.That(string.Join('|', login.Shown)).DoesNotContain("private-key");
            _ = await Assert.That(string.Join('|', list.Shown)).Contains("provider");
            _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsNull();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
