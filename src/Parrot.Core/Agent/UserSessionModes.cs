using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionModes(
    ModeRegistry modes,
    IReadOnlyDictionary<string, IModeWorkflowFactory> workflowFactories)
{
    private Dictionary<string, IModeWorkflow>? _workflows;

    public void Attach(AgentScratchDirectory mainScratch)
    {
        ArgumentNullException.ThrowIfNull(mainScratch);
        _workflows = workflowFactories.ToDictionary(
            static factory => factory.Key,
            factory => factory.Value.Create(mainScratch),
            StringComparer.Ordinal);
    }

    public IUserMode Resolve(string id)
    {
        var profile = modes.Resolve(id);

        return workflowFactories.ContainsKey(profile.Id)
            ? new SessionMode(
                profile,
                () => Workflow(profile.Id).RenderPrompt(profile),
                profile.SecurityProfile,
                () => Workflow(profile.Id).Prepare(),
                () => Workflow(profile.Id).Complete())
            : new SessionMode(
                profile,
                () => profile.Prompt,
                profile.SecurityProfile,
                static () => { },
                static () => ModeCompletionOutcome.None);
    }

    private IModeWorkflow Workflow(string id) =>
        _workflows?[id] ?? throw new ModeRegistryException("the main agent scratch directory is not attached");
}
