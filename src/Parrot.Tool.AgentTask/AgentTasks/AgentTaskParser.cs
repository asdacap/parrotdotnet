using System.Text.Json;

namespace Parrot.AgentTasks;

internal static class AgentTaskParser
{
    internal const int MaxDescriptionLength = 300;

    internal static AgentTaskArtifact ParseArtifact(string json)
    {
        var envelope = JsonEnvelope.Extract(json);
        var wire = Deserialize<AgentTaskArtifactWire>(envelope, AgentTaskWireJsonContext.Default.AgentTaskArtifactWire);
        using var document = JsonDocument.Parse(envelope);
        var root = document.RootElement;
        RequireObject(root, "artifact");
        RejectUnknown(root, "artifact", "schema_version", "tasks");
        RequireProperty(root, "schema_version", JsonValueKind.Number);
        RequireProperty(root, "tasks", JsonValueKind.Array);
        if (wire.SchemaVersion != AgentTaskArtifact.Version1)
        {
            throw new ArgumentException("schema_version must equal 1.");
        }

        var tasks = ParseTasks(root.GetProperty("tasks"), "tasks");
        ValidateGraph(tasks, "tasks");
        return new AgentTaskArtifact(AgentTaskArtifact.Version1, tasks);
    }

    /// <summary>Parses an upsert whose dependencies may name tasks set earlier, so only the merged graph is validated.</summary>
    internal static IReadOnlyList<AgentTask> ParseTaskSet(string json)
    {
        _ = Deserialize<AgentTaskWire[]>(json, AgentTaskWireJsonContext.Default.AgentTaskWireArray);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("tasks must be an array.");
        }

        return ParseTasks(root, "tasks");
    }

    internal static AgentTaskLeafResponse ParseLeafResponse(string json)
    {
        var envelope = JsonEnvelope.Extract(json);
        _ = Deserialize<AgentTaskLeafResponseWire>(envelope, AgentTaskWireJsonContext.Default.AgentTaskLeafResponseWire);
        using var document = JsonDocument.Parse(envelope);
        var root = document.RootElement;
        RequireObject(root, "leaf response");
        var result = RequiredString(root, "result", "leaf response");
        return new(result, ParseLeafVerdict(root));
    }

    /// <summary>Rejects a graph with a dependency on an absent task or a dependency cycle.</summary>
    internal static void ValidateGraph(IReadOnlyList<AgentTask> tasks, string path)
    {
        var lookup = tasks.ToDictionary(task => task.Name, StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            foreach (var dependency in task.Dependencies)
            {
                if (!lookup.ContainsKey(dependency))
                {
                    throw new ArgumentException($"{path} task '{task.Name}' has missing sibling dependency '{dependency}'.");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            Visit(task);
        }

        return;
        void Visit(AgentTask task)
        {
            if (visited.Contains(task.Name))
            {
                return;
            }

            if (!visiting.Add(task.Name))
            {
                throw new ArgumentException($"{path} contains a dependency cycle.");
            }

            foreach (var dependency in task.Dependencies)
            {
                Visit(lookup[dependency]);
            }

            _ = visiting.Remove(task.Name);
            _ = visited.Add(task.Name);
        }
    }

    private static AcceptanceVerdict ParseLeafVerdict(JsonElement root)
    {
        var verdict = RequiredString(root, "verdict", "leaf response");
        return verdict switch
        {
            "accept" => LeafAccept(root),
            "reject_and_halt" => LeafRejectAndHalt(root),
            "reject_and_retry" => LeafRejectAndRetry(root),
            _ => throw new ArgumentException("leaf response verdict must be accept, reject_and_halt, or reject_and_retry."),
        };
    }

    private static AcceptanceVerdict LeafAccept(JsonElement root)
    {
        RejectUnknown(root, "leaf response", "result", "verdict", "evidence");
        return new(AcceptanceVerdictKind.Accept, RequiredString(root, "evidence", "leaf response"), null, null, null);
    }

    private static AcceptanceVerdict LeafRejectAndHalt(JsonElement root)
    {
        RejectUnknown(root, "leaf response", "result", "verdict", "feedback");
        return new(AcceptanceVerdictKind.RejectAndHalt, null, RequiredString(root, "feedback", "leaf response"), null, null);
    }

    private static AcceptanceVerdict LeafRejectAndRetry(JsonElement root)
    {
        RejectUnknown(root, "leaf response", "result", "verdict", "feedback", "payload", "replacement_result");
        return new(
            AcceptanceVerdictKind.RejectAndRetry,
            null,
            RequiredString(root, "feedback", "leaf response"),
            OptionalString(root, "payload", "leaf response"),
            OptionalString(root, "replacement_result", "leaf response"));
    }

    private static List<AgentTask> ParseTasks(JsonElement element, string path)
    {
        if (element.GetArrayLength() == 0)
        {
            throw new ArgumentException($"{path} must not be empty.");
        }

        var tasks = new List<AgentTask>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            tasks.Add(ParseTask(item, $"{path}[{index}]"));
            index++;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (!names.Add(task.Name))
            {
                throw new ArgumentException($"{path} contains duplicate task name '{task.Name}'.");
            }

            if (task.Dependencies.Contains(task.Name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"{path} task '{task.Name}' cannot depend on itself.");
            }
        }

        return tasks;
    }

    private static AgentTask ParseTask(JsonElement element, string path)
    {
        RequireObject(element, path);
        RejectUnknown(element, path, "name", "dependencies", "description", "payload", "acceptance_criteria", "model", "hidden", "state", "result", "failure");
        var name = RequiredString(element, "name", path);
        var description = RequiredString(element, "description", path);
        if (description.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"{path} description must be at most {MaxDescriptionLength} characters.");
        }

        var criteria = RequiredString(element, "acceptance_criteria", path);
        var payload = ParsePayload(RequiredProperty(element, "payload", JsonValueKind.String, JsonValueKind.Array), $"{path} payload");
        return new AgentTask(
            name,
            ParseDependencies(element, path),
            description,
            payload,
            criteria,
            OptionalString(element, "model", path),
            OptionalBoolean(element, "hidden", path),
            ParseState(element, path),
            OptionalString(element, "result", path),
            OptionalString(element, "failure", path));
    }

    private static AgentTaskExecutionStatus ParseState(JsonElement task, string path) =>
        OptionalString(task, "state", path) switch
        {
            null or "pending" => AgentTaskExecutionStatus.Pending,
            "running" => AgentTaskExecutionStatus.Running,
            "succeeded" => AgentTaskExecutionStatus.Succeeded,
            "failed" => AgentTaskExecutionStatus.Failed,
            "canceled" => AgentTaskExecutionStatus.Canceled,
            _ => throw new ArgumentException($"{path} state must be pending, running, succeeded, failed, or canceled."),
        };

    private static AgentTaskPayload ParsePayload(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return AgentTaskPayload.FromInstruction(Nonblank(element.GetString(), path));
        }

        var tasks = ParseTasks(element, path);
        ValidateGraph(tasks, path);
        return AgentTaskPayload.FromTasks(tasks);
    }

    private static string[] ParseDependencies(JsonElement task, string path)
    {
        if (!task.TryGetProperty("dependencies", out var element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"{path} dependencies must be an array.");
        }

        var values = new List<string>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"{path} dependencies must contain strings.");
            }

            values.Add(Nonblank(value.GetString(), $"{path} dependency"));
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new ArgumentException($"{path} dependencies must be distinct.");
        }

        return [.. values];
    }

    private static string? OptionalString(JsonElement objectElement, string name, string path) =>
        objectElement.TryGetProperty(name, out var value)
            ? Nonblank(RequireString(value, $"{path} {name}"), $"{path} {name}")
            : null;

    private static bool OptionalBoolean(JsonElement objectElement, string name, string path)
    {
        if (!objectElement.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"{path} {name} must be a boolean."),
        };
    }

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo) ?? throw new ArgumentException("JSON must not be null.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Invalid JSON: {exception.Message}");
        }
    }

    private static JsonElement RequiredProperty(JsonElement element, string name, params JsonValueKind[] kinds)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw new ArgumentException($"{name} is required.");
        }

        if (!kinds.Contains(value.ValueKind))
        {
            throw new ArgumentException($"{name} has an invalid value.");
        }

        return value;
    }

    private static void RequireProperty(JsonElement element, string name, JsonValueKind kind) => _ = RequiredProperty(element, name, kind);

    private static string RequiredString(JsonElement element, string name, string path) => Nonblank(RequireString(RequiredProperty(element, name, JsonValueKind.String), $"{path} {name}"), $"{path} {name}");

    private static string RequireString(JsonElement value, string path) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? throw new ArgumentException($"{path} must not be null.") : throw new ArgumentException($"{path} must be a string.");

    private static string Nonblank(string? value, string path) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{path} must be nonblank.") : value;

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{path} must be an object.");
        }
    }

    private static void RejectUnknown(JsonElement element, string path, params string[] allowed)
    {
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!encountered.Add(property.Name))
            {
                throw new ArgumentException($"{path} contains duplicate field '{property.Name}'.");
            }

            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"{path} contains unknown field '{property.Name}'.");
            }
        }
    }
}
