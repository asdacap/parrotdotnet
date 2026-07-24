using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class TodoCollection(
    string agentSessionId, EventRepository repository, EventBroker eventBroker)
{
    public IReadOnlyList<TodoItem> Read(CancellationToken cancellationToken) =>
        repository.ReadTodos(agentSessionId, cancellationToken);

    public async Task<IReadOnlyList<TodoItem>> Replace(
        IReadOnlyList<TodoItem> items, CancellationToken cancellationToken)
    {
        var normalized = new List<TodoItem>(items.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (var position = 0; position < items.Count; position++)
        {
            var item = items[position];
            var content = item.Content.Trim();

            if (content.Length == 0)
            {
                throw new ArgumentException($"todo at position {position} has blank content", nameof(items));
            }

            if (!Enum.IsDefined(item.Status))
            {
                throw new ArgumentException($"todo at position {position} has an invalid status", nameof(items));
            }

            if (!Enum.IsDefined(item.Priority))
            {
                throw new ArgumentException($"todo at position {position} has an invalid priority", nameof(items));
            }

            var id = item.Id.Length == 0 ? Identifier.TodoId() : item.Id;

            if (!ids.Add(id))
            {
                throw new ArgumentException($"duplicate todo ID '{id}'", nameof(items));
            }

            normalized.Add(new TodoItem(id, content, item.Status, item.Priority, position));
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = agentSessionId,
            TodoUpdated = new TodoUpdated(),
        };

        foreach (var item in normalized)
        {
            published.TodoUpdated.Todos.Add(new TodoState
            {
                Id = item.Id,
                Content = item.Content,
                Status = StatusText(item.Status),
                Priority = PriorityText(item.Priority),
                Position = item.Position,
            });
        }

        repository.ReplaceTodos(agentSessionId, normalized, published, cancellationToken);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    private static string StatusText(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.InProgress => "in_progress",
        TodoStatus.Completed => "completed",
        TodoStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static string PriorityText(TodoPriority priority) => priority switch
    {
        TodoPriority.High => "high",
        TodoPriority.Medium => "medium",
        TodoPriority.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };
}
