using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
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
    string WorkingDirectory,
    string ConfigDirectory,
    string Date,
    ModelPromptContext ModelPromptContext,
    Compactor Compactor,
    MainAgentProfile? Profile,
    SecurityProfile SecurityProfile,
    RuntimeStatus? Status,
    CancellationToken Lifetime);
