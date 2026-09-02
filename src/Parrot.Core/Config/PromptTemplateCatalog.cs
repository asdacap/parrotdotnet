using System.Collections.ObjectModel;
using System.Text;

namespace Parrot.Config;

internal sealed class PromptTemplateCatalog
{
    private readonly ReadOnlyDictionary<string, PromptTemplate> _templates;

    public PromptTemplateCatalog(IReadOnlyDictionary<string, PromptTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = new ReadOnlyDictionary<string, PromptTemplate>(
            new Dictionary<string, PromptTemplate>(templates, StringComparer.Ordinal));
    }

    public static PromptTemplateCatalog DefaultAgentTaskTemplates { get; } = CreateDefaultAgentTaskTemplates();

    public string Render(string id, IReadOnlyList<PromptTemplateArgument> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_templates.TryGetValue(id, out var template))
        {
            throw new InvalidDataException($"prompt_templates.{id} is not defined");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            if (!template.Allowed.Contains(argument.Name))
            {
                throw new InvalidDataException($"prompt_templates.{id} does not allow argument '{argument.Name}'");
            }

            if (!values.TryAdd(argument.Name, argument.Value))
            {
                throw new InvalidDataException($"prompt_templates.{id} received duplicate argument '{argument.Name}'");
            }
        }

        foreach (var required in template.Required)
        {
            if (!values.ContainsKey(required))
            {
                throw new InvalidDataException($"prompt_templates.{id} requires argument '{required}'");
            }
        }

        var result = new StringBuilder(template.Text.Length);
        foreach (var part in template.Parts)
        {
            _ = result.Append(part.Placeholder is null ? part.Text : values[part.Placeholder]);
        }

        return result.ToString();
    }

    private static PromptTemplateCatalog CreateDefaultAgentTaskTemplates()
    {
        const string common = "Task: {task_name}\nDescription: {description}\nAcceptance criteria: {acceptance_criteria}\n{ancestors}{contexts}{dependencies}";
        return new(new Dictionary<string, PromptTemplate>(StringComparer.Ordinal)
        {
            ["agent-task.header"] = Create(
                "Task: {task_name}\nDescription: {description}\nAcceptance criteria: {acceptance_criteria}{ancestors}{contexts}{dependencies}",
                "task_name",
                "description",
                "acceptance_criteria",
                "ancestors",
                "contexts",
                "dependencies"),
            ["agent-task.feedback"] = Create("Retry feedback:{items}", "items"),
            ["agent-task.nested-results"] = Create("Nested task results (structured JSON):\n{results}", "results"),
            ["agent-task.research"] = Create(
                $"AgentTask role: retained composite research turn\n{common}\nCurrent task path: {{path}}\nOriginal current task declaration:\n{{declaration}}\nResearch and prepare this task as the retained composite agent. This agent will receive later execution and validation turns and owns any nested task agents. Return only strict JSON with no prose or code fence: {{research_response}}. Omit task_patch when no change is needed; when present omit every unchanged field.",
                "task_name",
                "description",
                "acceptance_criteria",
                "ancestors",
                "contexts",
                "dependencies",
                "path",
                "declaration",
                "research_response"),
            ["agent-task.execution"] = Create(
                $"AgentTask role: payload executor\n{common}{{feedback}}\nExecute this instruction and return the execution result:\n{{instruction}}",
                "task_name",
                "description",
                "acceptance_criteria",
                "ancestors",
                "contexts",
                "dependencies",
                "feedback",
                "instruction"),
            ["agent-task.leaf"] = Create(
                $"AgentTask role: payload executor\n{common}{{feedback}}\nInspect, implement, and verify this instruction:\n{{instruction}}\nReturn only one strict JSON object with no prose or code fence: {{leaf_response}}. Omit replacement_context to carry the returned current context into the next attempt.",
                "task_name",
                "description",
                "acceptance_criteria",
                "ancestors",
                "contexts",
                "dependencies",
                "feedback",
                "instruction",
                "leaf_response"),
            ["agent-task.acceptance"] = Create(
                $"AgentTask role: retained composite validation turn\n{common}{{feedback}}\nExecution result:\n{{execution}}{{nested}}\nReview the completed attempt as the retained composite agent that performed the research turn. Nested task agents are children owned by this composite agent. Return only one strict JSON object with no prose or code fence: {{acceptance_response}} A reject_and_retry context replaces this task's research context for later attempts and descendants; omit it to retain the existing context.",
                "task_name",
                "description",
                "acceptance_criteria",
                "ancestors",
                "contexts",
                "dependencies",
                "feedback",
                "execution",
                "nested",
                "acceptance_response"),
            ["agent-task.role-failure"] = Create("{role} agent {status}: {error}", "role", "status", "error"),
            ["agent-task.truncated"] = Create("{value}\n[truncated]", "value"),
        });
    }

    private static PromptTemplate Create(string text, params string[] arguments) =>
        new("prompt_templates.agent-task", text, new HashSet<string>(arguments, StringComparer.Ordinal), new HashSet<string>(arguments, StringComparer.Ordinal));
}
