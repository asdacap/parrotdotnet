using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Parrot.Queues;

namespace Parrot.Tools;

internal static class QueueToolExecution
{
    public static TInput Deserialize<TInput>(string json, JsonTypeInfo<TInput> typeInfo)
        where TInput : class =>
        JsonSerializer.Deserialize(json, typeInfo) ?? throw new FormatException("Tool arguments must be an object.");

    public static string RequireName(string? name) => name ?? throw new FormatException("Tool arguments require a string 'name'.");

    public static QueueDirection ParseDirection(string? direction) => direction switch { null => QueueDirection.Unspecified, "front" => QueueDirection.Front, "back" => QueueDirection.Back, _ => throw new FormatException("Tool argument 'direction' must be 'front' or 'back'.") };

    public static string Serialize(QueueInfo info) => JsonSerializer.Serialize(new QueueToolInfo { Path = info.Path, Name = info.Name, Description = info.Description.Length == 0 ? null : info.Description, Size = info.Size, Closed = info.Closed ? true : null }, QueueToolJsonContext.Default.QueueToolInfo);

    public static string Serialize(QueueInfo info, IReadOnlyList<string> items) => JsonSerializer.Serialize(
        new QueueTakeToolResult
        {
            Path = info.Path,
            Name = info.Name,
            Description = info.Description.Length == 0 ? null : info.Description,
            Size = info.Size,
            Closed = info.Closed,
            Items = [.. items],
        },
        QueueToolJsonContext.Default.QueueTakeToolResult);

    public static Task<ToolExecutionResult> Execute(ToolInvocation invocation, Func<string> action)
    {
        try
        {
            return Task.FromResult<ToolExecutionResult>(action());
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QueueException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }
    }
}
