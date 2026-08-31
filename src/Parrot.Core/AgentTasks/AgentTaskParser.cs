using System.Text.Json;

namespace Parrot.AgentTasks;

internal static class AgentTaskParser
{
    internal static AgentTaskArtifact ParseArtifact(string json)
    {
        var wire = Deserialize<AgentTaskArtifactWire>(json, AgentTaskWireJsonContext.Default.AgentTaskArtifactWire);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireObject(root, "artifact");
        RejectUnknown(root, "artifact", "schema_version", "tasks");
        RequireProperty(root, "schema_version", JsonValueKind.Number);
        RequireProperty(root, "tasks", JsonValueKind.Array);
        if (wire.SchemaVersion != AgentTaskArtifact.Version1)
        {
            throw Invalid("schema_version must equal 1.");
        }

        var tasks = ParseTasks(root.GetProperty("tasks"), "tasks");
        return new AgentTaskArtifact(AgentTaskArtifact.Version1, tasks);
    }

    internal static ResearchHookResult ParseResearchHook(string json)
    {
        _ = Deserialize<ResearchHookWire>(json, AgentTaskWireJsonContext.Default.ResearchHookWire);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireObject(root, "research hook");
        RejectUnknown(root, "research hook", "context", "task_patch");
        var context = RequiredString(root, "context", "research hook");
        AgentTaskPatch? patch = null;
        if (root.TryGetProperty("task_patch", out var patchElement))
        {
            if (patchElement.ValueKind == JsonValueKind.Null)
            {
                throw Invalid("research hook task_patch must not be null.");
            }

            patch = ParsePatch(patchElement, "research hook task_patch");
        }

        return new ResearchHookResult(context, patch);
    }

    internal static AcceptanceVerdict ParseVerdict(string json)
    {
        _ = Deserialize<AcceptanceVerdictWire>(json, AgentTaskWireJsonContext.Default.AcceptanceVerdictWire);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireObject(root, "acceptance verdict");
        var verdict = RequiredString(root, "verdict", "acceptance verdict");
        return verdict switch
        {
            "accept" => Accept(root),
            "reject" => Reject(root),
            "retry" => Retry(root),
            _ => throw Invalid("acceptance verdict verdict must be accept, reject, or retry."),
        };
    }

    internal static void ValidateEffective(EffectiveAgentTask task) =>
        _ = ParseTask(task.Name, task.Dependencies, task.Description, task.Payload, task.AcceptanceCriteria, task.Model, "effective task");

    private static AcceptanceVerdict Accept(JsonElement root)
    {
        RejectUnknown(root, "acceptance verdict", "verdict", "evidence");
        return new(AcceptanceVerdictKind.Accept, RequiredString(root, "evidence", "acceptance verdict"), null, null);
    }

    private static AcceptanceVerdict Reject(JsonElement root)
    {
        RejectUnknown(root, "acceptance verdict", "verdict", "feedback");
        return new(AcceptanceVerdictKind.Reject, null, RequiredString(root, "feedback", "acceptance verdict"), null);
    }

    private static AcceptanceVerdict Retry(JsonElement root)
    {
        RejectUnknown(root, "acceptance verdict", "verdict", "feedback", "payload");
        return new(
            AcceptanceVerdictKind.Retry,
            null,
            RequiredString(root, "feedback", "acceptance verdict"),
            ParsePayload(RequiredProperty(root, "payload", JsonValueKind.String, JsonValueKind.Array), "acceptance verdict payload"));
    }

    private static List<AgentTask> ParseTasks(JsonElement element, string path)
    {
        if (element.GetArrayLength() == 0)
        {
            throw Invalid($"{path} must not be empty.");
        }

        var tasks = new List<AgentTask>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            tasks.Add(ParseTask(item, $"{path}[{index}]"));
            index++;
        }

        ValidateSiblingGraph(tasks, path);
        return tasks;
    }

    private static AgentTask ParseTask(JsonElement element, string path)
    {
        RequireObject(element, path);
        RejectUnknown(element, path, "name", "dependencies", "description", "payload", "acceptance_criteria", "model");
        var name = RequiredString(element, "name", path);
        var description = RequiredString(element, "description", path);
        var criteria = RequiredString(element, "acceptance_criteria", path);
        var payload = ParsePayload(RequiredProperty(element, "payload", JsonValueKind.String, JsonValueKind.Array), $"{path} payload");
        var dependencies = ParseDependencies(element, path);
        var model = OptionalString(element, "model", path);
        return ParseTask(name, dependencies, description, payload, criteria, model, path);
    }

    private static AgentTask ParseTask(string name, IReadOnlyList<string> dependencies, string description, AgentTaskPayload payload, string criteria, string? model, string path)
    {
        RequireNonblank(name, $"{path} name");
        RequireNonblank(description, $"{path} description");
        RequireNonblank(criteria, $"{path} acceptance_criteria");
        if (model is not null)
        {
            RequireNonblank(model, $"{path} model");
        }

        if (payload.Instruction is not null)
        {
            RequireNonblank(payload.Instruction, $"{path} payload");
        }
        else if (payload.Tasks is null || payload.Tasks.Count == 0)
        {
            throw Invalid($"{path} payload is invalid.");
        }

        return new AgentTask(name, [.. dependencies], description, payload, criteria, model);
    }

    private static AgentTaskPayload ParsePayload(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.String => AgentTaskPayload.FromInstruction(Nonblank(element.GetString(), path)),
        JsonValueKind.Array => AgentTaskPayload.FromTasks(ParseTasks(element, path)),
        _ => throw Invalid($"{path} must be a nonblank string or nonempty task array."),
    };

    private static List<string> ParseDependencies(JsonElement task, string path)
    {
        if (!task.TryGetProperty("dependencies", out var element))
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid($"{path} dependencies must be an array.");
        }

        var values = new List<string>();
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                throw Invalid($"{path} dependencies must contain strings.");
            }

            values.Add(Nonblank(value.GetString(), $"{path} dependency"));
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw Invalid($"{path} dependencies must be distinct.");
        }

        return values;
    }

    private static AgentTaskPatch ParsePatch(JsonElement element, string path)
    {
        RequireObject(element, path);
        RejectUnknown(element, path, "description", "payload", "acceptance_criteria", "model");
        if (!element.EnumerateObject().Any())
        {
            throw Invalid($"{path} must contain a mutable field.");
        }

        var description = OptionalPatchString(element, "description", path);
        var criteria = OptionalPatchString(element, "acceptance_criteria", path);
        AgentTaskPayload? payload = null;
        if (element.TryGetProperty("payload", out var payloadElement))
        {
            payload = ParsePayload(RequireNotNull(payloadElement, $"{path} payload"), $"{path} payload");
        }

        var model = element.TryGetProperty("model", out var modelElement)
            ? OptionalValue<string>.From(Nonblank(RequireString(modelElement, $"{path} model"), $"{path} model"))
            : OptionalValue<string>.Unspecified;
        return new AgentTaskPatch(description, payload, criteria, model);
    }

    private static string? OptionalPatchString(JsonElement objectElement, string name, string path) =>
        objectElement.TryGetProperty(name, out var value)
            ? Nonblank(RequireString(value, $"{path} {name}"), $"{path} {name}")
            : null;

    private static string? OptionalString(JsonElement objectElement, string name, string path) =>
        objectElement.TryGetProperty(name, out var value)
            ? Nonblank(RequireString(value, $"{path} {name}"), $"{path} {name}")
            : null;

    private static void ValidateSiblingGraph(IReadOnlyList<AgentTask> tasks, string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (!names.Add(task.Name))
            {
                throw Invalid($"{path} contains duplicate task name '{task.Name}'.");
            }
        }

        foreach (var task in tasks)
        {
            foreach (var dependency in task.Dependencies)
            {
                if (dependency == task.Name)
                {
                    throw Invalid($"{path} task '{task.Name}' cannot depend on itself.");
                }

                if (!names.Contains(dependency))
                {
                    throw Invalid($"{path} task '{task.Name}' has missing sibling dependency '{dependency}'.");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var lookup = tasks.ToDictionary(task => task.Name, StringComparer.Ordinal);
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
                throw Invalid($"{path} contains a dependency cycle.");
            }

            foreach (var dependency in task.Dependencies)
            {
                Visit(lookup[dependency]);
            }

            _ = visiting.Remove(task.Name);
            _ = visited.Add(task.Name);
        }
    }

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo) ?? throw Invalid("JSON must not be null.");
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
            throw Invalid($"{name} is required.");
        }

        if (!kinds.Contains(value.ValueKind))
        {
            throw Invalid($"{name} has an invalid value.");
        }

        return value;
    }

    private static void RequireProperty(JsonElement element, string name, JsonValueKind kind) => _ = RequiredProperty(element, name, kind);

    private static JsonElement RequireNotNull(JsonElement element, string path) => element.ValueKind == JsonValueKind.Null ? throw Invalid($"{path} must not be null.") : element;

    private static string RequiredString(JsonElement element, string name, string path) => Nonblank(RequireString(RequiredProperty(element, name, JsonValueKind.String), $"{path} {name}"), $"{path} {name}");

    private static string RequireString(JsonElement value, string path) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? throw Invalid($"{path} must not be null.") : throw Invalid($"{path} must be a string.");

    private static string Nonblank(string? value, string path) => string.IsNullOrWhiteSpace(value) ? throw Invalid($"{path} must be nonblank.") : value;

    private static void RequireNonblank(string value, string path) => _ = Nonblank(value, path);

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{path} must be an object.");
        }
    }

    private static void RejectUnknown(JsonElement element, string path, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Invalid($"{path} contains unknown field '{property.Name}'.");
            }
        }
    }

    private static ArgumentException Invalid(string message) => new(message);
}
