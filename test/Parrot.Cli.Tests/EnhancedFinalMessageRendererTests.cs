using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedFinalMessageRendererTests
{
    [Test]
    [Arguments("{\"value\":1,\"text\":\"true\",\"null\":\"null\"}", "value: 1", "text: \"true\"", "\"null\": \"null\"")]
    [Arguments("[1,true,null]", "- 1", "- true", "- null")]
    [Arguments("\"hello\"", "\"hello\"", "", "")]
    [Arguments("42", "42", "", "")]
    [Arguments("true", "true", "", "")]
    [Arguments("null", "null", "", "")]
    public async Task Renders_complete_json_as_yaml(
        string source,
        string expectedFirst,
        string expectedSecond,
        string expectedThird)
    {
        var rendered = string.Join('\n', EnhancedFinalMessageRenderer.Render(source, "assistant: ", 80, false, true));

        _ = await Assert.That(rendered).Contains(expectedFirst);
        if (expectedSecond.Length > 0)
        {
            _ = await Assert.That(rendered).Contains(expectedSecond);
        }

        if (expectedThird.Length > 0)
        {
            _ = await Assert.That(rendered).Contains(expectedThird);
        }
    }

    [Test]
    public async Task Renders_non_json_and_ineligible_json_as_markdown()
    {
        var markdown = string.Join('\n', EnhancedFinalMessageRenderer.Render("**bold**", "assistant: ", 80, false, true));
        var ineligibleJson = string.Join('\n', EnhancedFinalMessageRenderer.Render("{\"value\":1}", "assistant: ", 80, false, false));

        _ = await Assert.That(markdown).Contains("bold");
        _ = await Assert.That(ineligibleJson).Contains("{\"value\":1}");
        _ = await Assert.That(ineligibleJson).DoesNotContain("value: 1");
    }
}
