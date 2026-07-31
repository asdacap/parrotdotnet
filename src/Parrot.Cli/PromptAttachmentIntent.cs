namespace Parrot.Cli;

internal sealed class PromptAttachmentIntent(PromptAttachmentIntentKind kind, string value)
{
    public PromptAttachmentIntentKind Kind { get; } = kind;

    public string Value { get; } = value;

    public static PromptAttachmentIntent Text(string value) => new(PromptAttachmentIntentKind.Text, value);

    public static PromptAttachmentIntent Path(string value) => new(PromptAttachmentIntentKind.Path, value);
}
