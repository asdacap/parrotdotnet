namespace Parrot.Auth;

// The bearer material a provider needs for one request: a fresh access token and
// the account id that routes it.
internal sealed record OAuthAccess(string AccessToken, string AccountId);
