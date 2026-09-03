using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

internal interface IAgentSessionScopeFactory
{
    IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentScope parentScope,
        ModelSelector model,
        ModelRouter router,
        EventBroker eventBroker,
        EventRepository eventRepository,
        ToolWorkspace workspace,
        ImageArtifactRepository images,
        WebFetcher webFetcher,
        AgentTaskConfig agentTasks,
        ToolDefinitionCatalog toolDefinitions,
        IReadOnlyList<string> readOnlyExecCommandPrefixes,
        ShellProcessOwners shellProcesses,
        ISystemPromptProvider systemPromptProvider,
        AgentScratchDirectory scratch,
        Compactor compactor,
        PromptTemplateCatalog promptTemplates,
        IMode mode,
        AgentSessionSecurity security,
        PermissionBroker permissions,
        RuntimeStatus status,
        AgentRegistry registry,
        AgentQueues queues,
        QuestionBroker userQuestions,
        TimeProvider timeProvider,
        CancellationToken lifetime);
}
