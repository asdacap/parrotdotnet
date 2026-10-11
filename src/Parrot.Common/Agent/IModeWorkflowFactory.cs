using Parrot.Store;

namespace Parrot.Agent;

/// <summary>Creates a mode's workflow for one user session.</summary>
internal interface IModeWorkflowFactory
{
    /// <summary>Creates a workflow whose state lives in the user session's main agent scratch directory.</summary>
    IModeWorkflow Create(AgentScratchDirectory mainScratch);
}
