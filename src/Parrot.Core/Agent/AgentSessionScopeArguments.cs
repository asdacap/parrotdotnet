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
    ProviderModel Model,
    EventBroker EventBroker,
    EventRepository EventRepository,
    IReadOnlyList<IToolFactory> ToolFactories,
    string WorkingDirectory,
    string ConfigDirectory,
    string Date,
    Compactor Compactor,
    ModeProfile? Mode,
    SecurityProfile SecurityProfile,
    RuntimeStatus? Status,
    CancellationToken Lifetime);
