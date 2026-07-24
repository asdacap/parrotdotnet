using Google.Protobuf;
using Parrot.Protocol;

namespace Parrot.Store;

// The durable half of the event stream. The event and its projection commit in
// one transaction (principle 9), so a reader can never observe an event whose
// projection is missing, nor the reverse.
//
// EventBroker publishes only what this has already committed, which is what
// keeps a subscriber from seeing an event a crash would un-happen.
internal sealed class EventRepository(SessionDatabase database)
{
    public void Append(Event published, string? messageRole, string? messageContent)
    {
        ArgumentNullException.ThrowIfNull(published);

        using var transaction = database.Begin();

        using (var insert = database.Connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO event (id, agent_session, payload, created_at) VALUES ($id, $session, $payload, $at);";
            _ = insert.Parameters.AddWithValue("$id", published.Id);
            _ = insert.Parameters.AddWithValue("$session", published.AgentSessionId);
            _ = insert.Parameters.AddWithValue("$payload", published.ToByteArray());
            _ = insert.Parameters.AddWithValue("$at", Timestamp());
            _ = insert.ExecuteNonQuery();
        }

        // The projection, in the same transaction. Not a second write that
        // might not happen.
        if (messageRole is not null && messageContent is not null)
        {
            using var project = database.Connection.CreateCommand();
            project.Transaction = transaction;
            project.CommandText =
                "INSERT INTO message (agent_session, role, content, created_at) VALUES ($session, $role, $content, $at);";
            _ = project.Parameters.AddWithValue("$session", published.AgentSessionId);
            _ = project.Parameters.AddWithValue("$role", messageRole);
            _ = project.Parameters.AddWithValue("$content", messageContent);
            _ = project.Parameters.AddWithValue("$at", Timestamp());
            _ = project.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<Event> Replay()
    {
        var events = new List<Event>();

        using var read = database.Connection.CreateCommand();
        read.CommandText = "SELECT payload FROM event ORDER BY sequence;";

        using var reader = read.ExecuteReader();

        while (reader.Read())
        {
            events.Add(Event.Parser.ParseFrom((byte[])reader["payload"]));
        }

        return events;
    }

    public IReadOnlyList<string> Messages(string agentSessionId)
    {
        var messages = new List<string>();

        using var read = database.Connection.CreateCommand();
        read.CommandText =
            "SELECT role, content FROM message WHERE agent_session = $session ORDER BY sequence;";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);

        using var reader = read.ExecuteReader();

        while (reader.Read())
        {
            messages.Add($"{reader["role"]}: {reader["content"]}");
        }

        return messages;
    }

    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
