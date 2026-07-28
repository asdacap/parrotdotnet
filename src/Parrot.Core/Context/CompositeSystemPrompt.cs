using Parrot.Agent;

namespace Parrot.Context;

internal sealed class CompositeSystemPrompt(IReadOnlyList<ISystemPrompt> prompts) : ISystemPrompt
{
    public void RenewEpoch()
    {
        foreach (var prompt in prompts)
        {
            prompt.RenewEpoch();
        }
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var sections = new List<string>(prompts.Count);

        foreach (var prompt in prompts)
        {
            var section = prompt.Build(selection);

            if (!string.IsNullOrWhiteSpace(section))
            {
                sections.Add(section);
            }
        }

        return string.Join("\n\n", sections);
    }
}
