using System.Text.Json;
using Parrot.Tools;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WaitToolPresenter(TimeProvider timeProvider) : IToolPresenter
{
    public string ToolName => "wait";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        LiveOnly = true,
        Modeline = true,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var remaining = new RemainingDuration(timeProvider, TimeSpan.FromMilliseconds(DurationMilliseconds(call.ArgumentsJson)));
        return new ToolLiveValue("Wait for incoming activity", [], Metadata, frame, () => $"{remaining.Format()} left");
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) => null;

    private static long DurationMilliseconds(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("duration_ms", out var duration)
            && duration.TryGetInt64(out var milliseconds)
                ? milliseconds
                : WaitTool.DefaultDurationMilliseconds;
    }
}
