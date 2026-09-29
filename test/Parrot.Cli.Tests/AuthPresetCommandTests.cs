using Parrot.Auth;
using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class AuthPresetCommandTests
{
    private static readonly Credential WorkKey = Credential.ForApiKey("work-key");
    private static readonly Credential PersonalKey = Credential.ForApiKey("personal-key");
    private static readonly Credential UnsavedKey = Credential.ForApiKey("unsaved-key");

    [Test]
    public async Task Presets_switch_accounts_sync_refreshed_tokens_and_confirm_before_dropping_unsaved_credentials(
        CancellationToken cancellationToken)
    {
        using var fixture = new PresetFixture();
        var credentials = fixture.Credentials;
        var workOAuth = OAuthCredential.Create("access-1", "refresh-1", DateTimeOffset.UnixEpoch, "account-a");
        var refreshedOAuth = OAuthCredential.Create("access-2", "refresh-2", DateTimeOffset.UnixEpoch, "account-a");

        await credentials.Set("provider", WorkKey, cancellationToken);
        await credentials.Set("chatgpt", Credential.ForOAuth(workOAuth), cancellationToken);
        var saveWork = new TestSlashDialog();
        await fixture.Set(saveWork).Run("work", cancellationToken);
        await credentials.Delete("chatgpt", cancellationToken);
        await credentials.Set("provider", PersonalKey, cancellationToken);
        await fixture.Set(new TestSlashDialog()).Run("personal", cancellationToken);

        var pickWork = new TestSlashDialog().Select("work");
        await fixture.Select(pickWork).Run(string.Empty, cancellationToken);
        _ = await Assert.That(await credentials.List(cancellationToken)).IsEquivalentTo(["chatgpt", "provider"]);
        _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsEqualTo(WorkKey);

        await credentials.Set("chatgpt", Credential.ForOAuth(refreshedOAuth), cancellationToken);
        var pickPersonal = new TestSlashDialog();
        await fixture.Select(pickPersonal).Run("personal", cancellationToken);
        _ = await Assert.That(await credentials.List(cancellationToken)).IsEquivalentTo(["provider"]);
        await fixture.Select(new TestSlashDialog()).Run("work", cancellationToken);
        _ = await Assert.That(await credentials.Get("chatgpt", cancellationToken))
            .IsEqualTo(Credential.ForOAuth(refreshedOAuth));

        await credentials.Set("provider", UnsavedKey, cancellationToken);
        var declined = new TestSlashDialog().Confirmation(false);
        await fixture.Select(declined).Run("personal", cancellationToken);
        _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsEqualTo(UnsavedKey);
        var accepted = new TestSlashDialog().Confirmation(true);
        await fixture.Select(accepted).Run("personal", cancellationToken);
        _ = await Assert.That(await credentials.Get("provider", cancellationToken)).IsEqualTo(PersonalKey);

        _ = await Assert.That(saveWork.Shown).IsEquivalentTo(["Credential preset saved: work (chatgpt, provider)"]);
        _ = await Assert.That(pickWork.Pickers.Single().Options.Select(option => option.Id))
            .IsEquivalentTo(["personal", "work"]);
        _ = await Assert.That(pickWork.Confirmed.Count + pickPersonal.Confirmed.Count).IsEqualTo(0);
        _ = await Assert.That(declined.Shown).IsEquivalentTo(
        [
            "Stored credentials (chatgpt, provider) do not match any saved preset.",
            "Selecting personal replaces them; save them first with /auth-preset-set. Continue?",
        ]);
        _ = await Assert.That(accepted.Shown.Last()).IsEqualTo("Credential preset selected: personal");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.PresetDirectory, "work.json"), cancellationToken))
            .Contains("refresh-2").And.DoesNotContain("refresh-1");
    }

    [Test]
    [Arguments("set", "bad name", false, false, "usage: /auth-preset-set <name>; name must be one token without whitespace or '/'")]
    [Arguments("set", "work", false, false, "no credentials are stored")]
    [Arguments("select", "bad name", true, true, "usage: /auth-preset-select <name>; name must be one token without whitespace or '/'")]
    [Arguments("select", "missing", true, true, "auth: credential preset not found")]
    [Arguments("select", "", false, false, "no credential presets are saved")]
    [Arguments("select", "", true, true, "")]
    public async Task Invalid_requests_report_an_error_and_leave_credentials_unchanged(
        string command, string arguments, bool stored, bool saved, string expectedError, CancellationToken cancellationToken)
    {
        using var fixture = new PresetFixture();
        if (stored)
        {
            await fixture.Credentials.Set("provider", WorkKey, cancellationToken);
        }

        if (saved)
        {
            await fixture.Set(new TestSlashDialog()).Run("work", cancellationToken);
            await fixture.Credentials.Set("provider", PersonalKey, cancellationToken);
        }

        var dialog = new TestSlashDialog().Select((string?)null);
        ISlashCommand slashCommand = command == "set" ? fixture.Set(dialog) : fixture.Select(dialog);
        await slashCommand.Run(arguments, cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Errors)).IsEqualTo(expectedError);
        _ = await Assert.That(dialog.Confirmed).IsEmpty();
        _ = await Assert.That(await fixture.Credentials.Get("provider", cancellationToken))
            .IsEqualTo(saved ? PersonalKey : stored ? WorkKey : null);
    }

    private sealed class PresetFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"parrot-credential-presets-{Guid.NewGuid():N}");
        private readonly FileCredentialStore _store;

        public PresetFixture()
        {
            _store = new FileCredentialStore(Path.Combine(_directory, "credentials.json"));
            Presets = new CredentialPresets(PresetDirectory);
        }

        public ICredentialStore Credentials => _store;

        public string PresetDirectory => Path.Combine(_directory, "credential_presets");

        public CredentialPresets Presets { get; }

        public AuthPresetSetCommand Set(TestSlashDialog dialog) => new(Presets, Credentials, dialog);

        public AuthPresetSelectCommand Select(TestSlashDialog dialog) => new(Presets, Credentials, new TestSlashActivity(), dialog);

        public void Dispose()
        {
            _store.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
