namespace Parrot.Agent;

internal sealed record TodoItem(
    string Id,
    string Content,
    TodoStatus Status,
    TodoPriority Priority,
    int Position);
