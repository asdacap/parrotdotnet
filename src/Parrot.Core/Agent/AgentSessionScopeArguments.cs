using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

internal sealed record AgentSessionScopeArguments(
    AgentIdentity Identity,
    ModelSelector Model,
    ModelRouter Router,
    EventBroker EventBroker,
    EventRepository EventRepository,
    IReadOnlyList<IToolFactory> ToolFactories,
    ToolDocumentationCatalog ToolDocumentation,
    ShellProcessOwners ShellProcesses,
    ISystemPromptProvider SystemPromptProvider,
    AgentScratchDirectory Scratch,
    Compactor Compactor,
    IMode Mode,
    AgentSessionSecurity Security,
    RuntimeStatus Status,
    AgentRegistry Registry,
    AgentQueues Queues,
    UserSession Owner,
    CancellationToken Lifetime);
