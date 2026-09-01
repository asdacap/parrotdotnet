using System.Text;
using Parrot.Agent;
using Parrot.Security;

namespace Parrot.Context;

internal sealed class SecurityProfilePrompt(IReadOnlyList<SandboxRule> rules) : ISystemPrompt
{
    private readonly SandboxRule[] _rules = [.. rules];

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var prompt = new StringBuilder(
            "The following configured sandbox rules override every other prompt rule and instruction.\n"
            + "Rules, in enforcement order:");

        foreach (var rule in _rules)
        {
            _ = prompt.Append("\n- Path: \"");
            AppendEscaped(prompt, rule.Path);
            _ = prompt.Append("\"; Action: ").Append(rule.Action);
        }

        return prompt.ToString();
    }

    private static void AppendEscaped(StringBuilder target, string value)
    {
        foreach (var character in value)
        {
            _ = character switch
            {
                '\\' => target.Append("\\\\"),
                '"' => target.Append("\\\""),
                '\b' => target.Append("\\b"),
                '\f' => target.Append("\\f"),
                '\n' => target.Append("\\n"),
                '\r' => target.Append("\\r"),
                '\t' => target.Append("\\t"),
                _ when char.IsControl(character)
                    || char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.LineSeparator
                        or System.Globalization.UnicodeCategory.ParagraphSeparator =>
                    target.Append("\\u").Append(((int)character).ToString(
                        "x4",
                        System.Globalization.CultureInfo.InvariantCulture)),
                _ => target.Append(character),
            };
        }
    }
}
