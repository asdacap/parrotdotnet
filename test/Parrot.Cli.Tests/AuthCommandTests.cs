using Parrot.Auth;
using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class AuthCommandTests
{
    [Test]
    public async Task Api_key_login_list_and_logout_follow_the_wizard_without_leaking_the_secret(
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"parrot-credentials-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "credentials.json");
        try
        {
            using var credentialStoreOwner = new FileCredentialStore(path);
            ICredentialStore credentials = credentialStoreOwner;
            using var http = new HttpClient();
            IOAuthClient oauth = new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions());
            var login = new TestSlashDialog().Select("login", "provider").Secret("  private-key  ");
            var list = new TestSlashDialog().Select("list");
            var logout = new TestSlashDialog().Select("logout", "provider");
            ISlashCommand command = new AuthCommand(credentials, oauth, ["provider"], login);
            ISlashCommand listCommand = new AuthCommand(credentials, oauth, ["provider"], list);
            ISlashCommand logoutCommand = new AuthCommand(credentials, oauth, ["provider"], logout);

            await command.Run(string.Empty, cancellationToken);
            _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsNotNull();
            await listCommand.Run(string.Empty, cancellationToken);
            await logoutCommand.Run(string.Empty, cancellationToken);

            _ = await Assert.That(string.Join('|', login.Shown)).Contains("stored a credential for provider");
            _ = await Assert.That(string.Join('|', login.Shown)).DoesNotContain("private-key");
            _ = await Assert.That(string.Join('|', list.Shown)).Contains("provider");
            _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsNull();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
