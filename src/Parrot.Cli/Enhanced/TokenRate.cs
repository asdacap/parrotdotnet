namespace Parrot.Cli.Enhanced;

internal readonly record struct TokenRate(decimal InputTokensPerSecond, decimal OutputTokensPerSecond)
{
    public bool HasTokens => InputTokensPerSecond > 0 || OutputTokensPerSecond > 0;
}
