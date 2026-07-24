using Parrot.Agent;

namespace Parrot.Tools;

internal static class TodoTools
{
    public static TodoItem ToDomain(TodoWireItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var content = item.Content ?? throw new FormatException("Todo item requires string 'content'.");
        var status = item.Status switch
        {
            "pending" => TodoStatus.Pending,
            "in_progress" => TodoStatus.InProgress,
            "completed" => TodoStatus.Completed,
            "cancelled" => TodoStatus.Cancelled,
            _ => throw new FormatException("Todo item has an invalid 'status'."),
        };
        var priority = item.Priority switch
        {
            "high" => TodoPriority.High,
            "medium" => TodoPriority.Medium,
            "low" => TodoPriority.Low,
            _ => throw new FormatException("Todo item has an invalid 'priority'."),
        };

        if (item.Position is not null)
        {
            throw new FormatException("Todo item contains an unexpected 'position'.");
        }

        return new TodoItem(item.Id ?? string.Empty, content, status, priority, 0);
    }

    public static TodoWireItem ToWire(TodoItem item) => new()
    {
        Id = item.Id,
        Content = item.Content,
        Status = StatusText(item.Status),
        Priority = PriorityText(item.Priority),
        Position = item.Position,
    };

    public static string StatusText(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.InProgress => "in_progress",
        TodoStatus.Completed => "completed",
        TodoStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string PriorityText(TodoPriority priority) => priority switch
    {
        TodoPriority.High => "high",
        TodoPriority.Medium => "medium",
        TodoPriority.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };
}
