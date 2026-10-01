using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class StandingInstruction(IPromptTemplateCatalog templates) : ISystemPrompt
{
    private string _instruction = string.Empty;

    public void Update(string instruction) => Volatile.Write(ref _instruction, instruction);

    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var instruction = Volatile.Read(ref _instruction);
        return instruction.Length == 0
            ? string.Empty
            : templates.Render("context.standing-instruction", [new PromptTemplateArgument("instruction", instruction)]);
    }
}
