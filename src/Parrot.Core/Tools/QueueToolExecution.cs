using System.Text.Json;
using Parrot.Queues;

namespace Parrot.Tools;

internal static class QueueToolExecution
{
    public static QueueToolInput Deserialize(string json) => JsonSerializer.Deserialize(json, QueueToolJsonContext.Default.QueueToolInput) ?? throw new FormatException("Tool arguments must be an object.");

    public static string RequireName(QueueToolInput input) => input.Name ?? throw new FormatException("Tool arguments require a string 'name'.");

    public static QueueDirection ParseDirection(string? direction) => direction switch { null => QueueDirection.Unspecified, "front" => QueueDirection.Front, "back" => QueueDirection.Back, _ => throw new FormatException("Tool argument 'direction' must be 'front' or 'back'.") };

    public static string Serialize(QueueInfo info) => JsonSerializer.Serialize(new QueueToolInfo { Path = info.Path, Name = info.Name, Description = info.Description.Length == 0 ? null : info.Description, Size = info.Size, Monitored = info.Monitored ? true : null }, QueueToolJsonContext.Default.QueueToolInfo);

    public static string Serialize(QueueInfo info, IReadOnlyList<string> items) => JsonSerializer.Serialize(
        new QueueTakeToolResult
        {
            Path = info.Path,
            Name = info.Name,
            Description = info.Description.Length == 0 ? null : info.Description,
            Size = info.Size,
            Monitored = info.Monitored ? true : null,
            Items = [.. items],
        },
        QueueToolJsonContext.Default.QueueTakeToolResult);

    public static Task<string> Execute(Func<string> action)
    {
        try
        {
            return Task.FromResult(action());
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QueueException)
        {
            return Task.FromResult($"error: {failure.Message}");
        }
    }
}
