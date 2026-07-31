namespace Parrot.Cli.Tests;

internal sealed class PromptAttachmentParserTests
{
    [Test]
    public async Task Parses_text_and_token_boundary_paths_in_order()
    {
        var intents = PromptAttachmentParser.Parse("Describe @first.png then @{second image.jpg} now");

        _ = await Assert.That(Format(intents)).IsEqualTo(
            "Text:Describe |Path:first.png|Text: then |Path:second image.jpg|Text: now");
    }

    [Test]
    public async Task Parses_an_image_only_prompt()
    {
        var intents = PromptAttachmentParser.Parse("@{reference image.png}");

        _ = await Assert.That(Format(intents)).IsEqualTo("Path:reference image.png");
    }

    [Test]
    public async Task Keeps_non_boundary_and_malformed_references_as_text()
    {
        var intents = PromptAttachmentParser.Parse("mail@example.com @{ } @{unfinished");

        _ = await Assert.That(Format(intents)).IsEqualTo("Text:mail@example.com @{ } @{unfinished");
    }

    [Test]
    public async Task Unescapes_a_token_boundary_double_at_sign()
    {
        var intents = PromptAttachmentParser.Parse("Use @@literal and @@{literal} @image.png");

        _ = await Assert.That(Format(intents)).IsEqualTo("Text:Use @literal and @{literal} |Path:image.png");
    }

    private static string Format(IEnumerable<PromptAttachmentIntent> intents) => string.Join(
        '|',
        intents.Select(intent => $"{intent.Kind}:{intent.Value}"));
}
