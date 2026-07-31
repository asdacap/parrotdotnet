using Parrot.Auth;
using Parrot.Config;

namespace Parrot.Llm;

internal sealed record ProviderBuildContext(
    string Id,
    ProviderConfig Config,
    ICredentialStore CredentialStore,
    HttpClient HttpClient,
    IBrowserOpener BrowserOpener);
