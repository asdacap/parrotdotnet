using System.Globalization;
using Parrot.Agent;
using Parrot.Config;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class StatisticsStatusProvider(
    AgentSessionStatisticsSnapshot snapshot,
    PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:statistics";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var views = new ScriptArray();
        AppendView(views, snapshot.Self, false);
        AppendView(views, snapshot.Cumulative, true);
        return ValueTask.FromResult(StatusObservation.AvailableText(templates.RenderStructured(
            "status.statistics",
            new ScriptObject
            {
                ["session_id"] = query.SessionId,
                ["views"] = views,
                ["incomplete_legacy_tool_counts"] = snapshot.HasIncompleteLegacyToolCounts,
            },
            cancellationToken)));
    }

    private static void AppendView(ScriptArray views, AgentUsageSnapshot usage, bool cumulative)
    {
        var view = new ScriptObject { ["cumulative"] = cumulative };
        AppendTotals(view, usage.Totals);
        var models = new ScriptArray();
        foreach (var (key, totals) in usage.Models
            .OrderBy(item => item.Key.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Model, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Effort, StringComparer.Ordinal))
        {
            var model = new ScriptObject
            {
                ["provider"] = key.Provider,
                ["model"] = key.Model,
                ["effort"] = key.Effort ?? string.Empty,
                ["has_effort"] = key.Effort is not null,
                ["legacy"] = key == AgentUsageKey.Legacy,
            };
            AppendTotals(model, totals);
            models.Add(model);
        }

        view.Add("models", models);
        views.Add(view);
    }

    private static void AppendTotals(ScriptObject model, AgentUsageTotals totals)
    {
        model.Add("input_tokens", TokenCountFormatter.Format(totals.InputTokens));
        model.Add("cache_percentage", (totals.InputTokens > 0
            ? (double)totals.CachedInputTokens / totals.InputTokens * 100
            : 0).ToString("F2", CultureInfo.InvariantCulture));
        model.Add("output_tokens", TokenCountFormatter.Format(totals.OutputTokens));
        model.Add("tool_calls", totals.ToolCalls.ToString(CultureInfo.InvariantCulture));
        model.Add("input_cost", totals.InputCost.ToString("F2", CultureInfo.InvariantCulture));
        model.Add("output_cost", totals.OutputCost.ToString("F2", CultureInfo.InvariantCulture));
        model.Add("total_cost", totals.TotalCost.ToString("F2", CultureInfo.InvariantCulture));
    }
}
