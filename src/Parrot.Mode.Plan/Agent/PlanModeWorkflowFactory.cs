using Parrot.Config;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class PlanModeWorkflowFactory(IPromptTemplateCatalog promptTemplates) : IModeWorkflowFactory
{
    public IModeWorkflow Create(AgentScratchDirectory mainScratch) => new PlanModeWorkflow(mainScratch, promptTemplates);
}
