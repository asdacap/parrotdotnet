using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
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
    ShellProcessOwners ShellProcesses,
    ISystemPromptProvider SystemPromptProvider,
    string BlobDirectory,
    Compactor Compactor,
    IAgentProfile? Profile,
    SecurityProfile SecurityProfile,
    RuntimeStatus? Status,
    AgentRegistry Registry,
    UserSession Owner,
    CancellationToken Lifetime);
