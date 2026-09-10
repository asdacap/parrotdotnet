using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Skills;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactory(
    UserSession owner,
    string workingDirectory,
    Compactor compactor,
    WebFetcher webFetcher,
    ToolDefinitionCatalog toolDefinitions,
    AgentTaskConfig agentTasks,
    RequestLimitsConfig requestLimits,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    ModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    PromptTemplateCatalog promptTemplates) : IAgentSessionFactory
{
    private readonly ImageArtifactRepository _images = owner.Images;

    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        IAgentRegistry registry,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(parentLink);
        var queues = owner.QueueCatalog.Register(identity);
        try
        {
            var scratch = owner.Resources.AgentScratch(identity.SessionId);
            _ = eventRepository.PrepareAgentHistory(identity.SessionId);
            var workspace = new ToolWorkspace(workingDirectory);
            var pathEnvironment = new AgentPathEnvironment(owner.Resources, scratch);
            var security = new AgentSessionSecurity(
                securityProfile,
                owner.Resources.Workspace,
                owner.Resources.ScratchRootDirectory);
            var agentSkills = new AgentSkills(owner.SkillCatalog, promptTemplates);
            var prompts = new CompositeSystemPromptProvider(
                "runtime:agent-session-system-prompt",
                [
                    systemPromptProvider,
                    new AgentPathEnvironmentProvider(pathEnvironment, promptTemplates),
                    new ScratchDirectoryProvider(scratch, promptTemplates),
                    new AgentSkillPromptProvider(agentSkills),
                ]);
            var arguments = new AgentSessionScopeArguments(
                identity,
                parentLink,
                model,
                router,
                eventBroker,
                eventRepository,
                workspace,
                _images,
                webFetcher,
                agentTasks,
                requestLimits,
                owner.AgentTaskRuns,
                toolDefinitions,
                readOnlyExecCommandPrefixes,
                owner.ShellProcesses,
                prompts,
                scratch,
                pathEnvironment,
                compactor,
                promptTemplates,
                mode,
                security,
                agentSkills,
                owner.Permissions,
                status,
                registry,
                queues,
                owner.Questions,
                owner.TimeProvider,
                owner.Diagnostics,
                lifetime);
            var scope = new AgentSessionScope(arguments);
            try
            {
                queues.Attach(scope.Session);
                owner.ShellProcesses.Register(scope.Processes);
                return scope;
            }
            catch
            {
                scope.DisposeRejectedConstruction();
                throw;
            }
        }
        catch
        {
            queues.Dispose();
            throw;
        }
    }
}
