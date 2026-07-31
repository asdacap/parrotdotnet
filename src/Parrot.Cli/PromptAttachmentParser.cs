using System.Text;

namespace Parrot.Cli;

internal static class PromptAttachmentParser
{
    public static IReadOnlyList<PromptAttachmentIntent> Parse(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var intents = new List<PromptAttachmentIntent>();
        var text = new StringBuilder();
        var index = 0;
        while (index < prompt.Length)
        {
            if (prompt[index] != '@' || !IsTokenBoundary(prompt, index))
            {
                _ = text.Append(prompt[index++]);
                continue;
            }

            if (index + 1 < prompt.Length && prompt[index + 1] == '@')
            {
                _ = text.Append('@');
                index += 2;
                continue;
            }

            if (TryReadPath(prompt, index, out var path, out var next))
            {
                AppendText(intents, text);
                intents.Add(PromptAttachmentIntent.Path(path));
                index = next;
                continue;
            }

            _ = text.Append(prompt[index++]);
        }

        AppendText(intents, text);
        return intents;
    }

    private static bool IsTokenBoundary(string prompt, int index) => index == 0 || char.IsWhiteSpace(prompt[index - 1]);

    private static bool TryReadPath(string prompt, int index, out string path, out int next)
    {
        var pathStart = index + 1;
        if (pathStart >= prompt.Length)
        {
            path = string.Empty;
            next = index;
            return false;
        }

        if (prompt[pathStart] == '{')
        {
            var closingBrace = prompt.IndexOf('}', pathStart + 1);
            if (closingBrace <= pathStart + 1)
            {
                path = string.Empty;
                next = index;
                return false;
            }

            path = prompt[(pathStart + 1)..closingBrace];
            if (string.IsNullOrWhiteSpace(path))
            {
                path = string.Empty;
                next = index;
                return false;
            }

            next = closingBrace + 1;
            return true;
        }

        var pathEnd = pathStart;
        while (pathEnd < prompt.Length && !char.IsWhiteSpace(prompt[pathEnd]))
        {
            pathEnd++;
        }

        if (pathEnd == pathStart)
        {
            path = string.Empty;
            next = index;
            return false;
        }

        path = prompt[pathStart..pathEnd];
        next = pathEnd;
        return true;
    }

    private static void AppendText(List<PromptAttachmentIntent> intents, StringBuilder text)
    {
        if (text.Length == 0)
        {
            return;
        }

        intents.Add(PromptAttachmentIntent.Text(text.ToString()));
        _ = text.Clear();
    }
}
