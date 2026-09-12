using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;

namespace Parrot.Tools;

internal sealed class CompactContextTool(
    IAgentSession session,
    PromptTemplateCatalog promptTemplates) : ITool
{
    public string Name => "compact_context";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ContextSize? target = null;
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                CompactContextToolJsonContext.Default.CompactContextToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            if (input.TargetContextSize is { } targetText)
            {
                if (targetText.Length == 0)
                {
                    throw new FormatException("target_context_size must not be empty.");
                }

                target = ContextSize.Parse(targetText);
            }
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        var operation = await session.CompactFromTool(selection, target, cancellationToken).ConfigureAwait(false);
        var context = operation.Context;
        var result = RenderResult(operation.Reduced, context);
        const int maximumConvergenceAttempts = 8;
        for (var attempt = 0; attempt < maximumConvergenceAttempts; attempt++)
        {
            var renderedContext = session.EstimateContextAfterToolResult(selection, invocation.CallId, result);
            var renderedResult = RenderResult(operation.Reduced, renderedContext);
            context = renderedContext;
            if (string.Equals(result, renderedResult, StringComparison.Ordinal))
            {
                return result;
            }

            result = renderedResult;
        }

        return RenderResult(operation.Reduced, context);
    }

    private string RenderResult(bool reduced, ContextSnapshot context)
    {
        List<PromptTemplateArgument> arguments =
        [
            new("estimated_tokens", context.EstimatedTokens.ToString(CultureInfo.InvariantCulture)),
            new("context_limit", context.ContextLimit.ToString(CultureInfo.InvariantCulture)),
            new("notification_interval", ContextCadence.NotificationInterval.ToString(CultureInfo.InvariantCulture)),
            new("trigger", context.TriggerPercent.ToString(CultureInfo.InvariantCulture)),
        ];
        if (context.HasContextLimitOverride)
        {
            arguments.Add(new("trigger_tokens", context.TriggerTokens?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"));
        }

        var suffix = context.HasContextLimitOverride ? "-with-limit" : string.Empty;
        if (!context.IsAvailable)
        {
            return promptTemplates.Render($"compact-context-tool.unavailable{suffix}", arguments);
        }

        var usage = context.UsagePercent?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        return promptTemplates.Render(
            reduced ? $"compact-context-tool.compacted{suffix}" : $"compact-context-tool.no-op{suffix}",
            [.. arguments, new PromptTemplateArgument("usage", usage)]);
    }

    internal sealed class Input
    {
        [JsonPropertyName("target_context_size")]
        public string? TargetContextSize { get; init; }
    }
}
