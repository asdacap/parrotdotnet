using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
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
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    ModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    PromptTemplateCatalog promptTemplates,
    IAgentSessionScopeFactory scopes) : IAgentSessionFactory
{
    private readonly ImageArtifactRepository _images = owner.Images;

    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentScope parentScope,
        ModelSelector model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        AgentRegistry registry,
        CancellationToken lifetime)
    {
        parentScope.Validate(identity);
        var queues = owner.QueueCatalog.Register(identity);
        try
        {
            var scratch = owner.Resources.AgentScratch(identity.SessionId);
            _ = eventRepository.PrepareAgentHistory(identity.SessionId);
            var workspace = new ToolWorkspace(workingDirectory);
            var security = new AgentSessionSecurity(
                securityProfile,
                owner.Resources.Workspace,
                owner.Resources.ScratchRootDirectory);
            var prompts = new CompositeSystemPromptProvider(
                "runtime:agent-session-system-prompt",
                [systemPromptProvider, new ScratchDirectoryProvider(scratch, promptTemplates)]);
            return scopes.Create(
                identity,
                parentScope,
                model,
                router,
                eventBroker,
                eventRepository,
                workspace,
                _images,
                webFetcher,
                agentTasks,
                toolDefinitions,
                readOnlyExecCommandPrefixes,
                owner.ShellProcesses,
                prompts,
                scratch,
                compactor,
                promptTemplates,
                mode,
                security,
                owner.Permissions,
                status,
                registry,
                queues,
                owner.Questions,
                owner.TimeProvider,
                lifetime);
        }
        catch
        {
            queues.Dispose();
            throw;
        }
    }
}
