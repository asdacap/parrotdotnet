namespace Parrot.Llm;

// An assistant message exactly as one provider produced it, opaque to everything
// but that provider. Signed reasoning inside it is bound to the original content,
// so it is replayed verbatim and only to the provider that produced it.
internal sealed record LLMProviderContent(string ProviderId, string Content)
{
    public static LLMProviderContent None { get; } = new(string.Empty, string.Empty);

    // Whether this content can be replayed to the given provider.
    public bool IsFrom(string providerId) => Content.Length > 0 && ProviderId == providerId;
}
