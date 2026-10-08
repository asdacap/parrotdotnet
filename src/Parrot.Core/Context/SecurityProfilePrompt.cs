using System.Text;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Security;
using Scriban.Runtime;

namespace Parrot.Context;

internal sealed class SecurityProfilePrompt(
    IReadOnlyList<SandboxRule> rules,
    bool sandboxEnabled,
    IPromptTemplateCatalog templates) : ISystemPrompt
{
    private readonly Lazy<string> _rendered = new(() =>
    {
        var renderedRules = new ScriptArray();
        foreach (var rule in rules)
        {
            renderedRules.Add(new ScriptObject
            {
                ["path"] = Escape(rule.Path),
                ["action"] = rule.Action.ToString(),
            });
        }

        return templates.RenderStructured(
            "context.security-profile",
            new ScriptObject
            {
                ["rules"] = renderedRules,
                ["sandbox_enabled"] = sandboxEnabled,
            },
            CancellationToken.None);
    });

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return _rendered.Value;
    }

    private static string Escape(string value)
    {
        var target = new StringBuilder();
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

        return target.ToString();
    }
}
