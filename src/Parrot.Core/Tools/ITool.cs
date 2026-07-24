namespace Parrot.Tools;

// A tool the model may call. Display or behavioural differences live on the
// tool -- never a branch on its name elsewhere (AGENTS.md). ParametersJson is
// the JSON Schema the tool owns; the registry forwards it, the provider sends
// it.
internal interface ITool
{
    string Name { get; }

    string Description { get; }

    string ParametersJson { get; }

    Task<string> Execute(string argumentsJson, IToolContext context, CancellationToken cancellationToken);
}
