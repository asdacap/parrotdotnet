using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

internal sealed record AgentSessionScopeArguments(
    AgentIdentity Identity,
    AgentSessionParentScope ParentScope,
    ModelSelector Model,
    ModelRouter Router,
    EventBroker EventBroker,
    EventRepository EventRepository,
    IReadOnlyList<IToolFactory> ToolFactories,
    ToolDefinitionCatalog ToolDefinitions,
    IReadOnlyList<string> ReadOnlyExecCommandPrefixes,
    ShellProcessOwners ShellProcesses,
    ISystemPromptProvider SystemPromptProvider,
    AgentScratchDirectory Scratch,
    Compactor Compactor,
    PromptTemplateCatalog PromptTemplates,
    IMode Mode,
    AgentSessionSecurity Security,
    RuntimeStatus Status,
    AgentRegistry Registry,
    AgentQueues Queues,
    QuestionBroker UserQuestions,
    TimeProvider TimeProvider,
    CancellationToken Lifetime);
