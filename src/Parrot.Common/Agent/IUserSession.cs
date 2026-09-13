using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Skills;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

/// <summary>Owns a working directory's agents, resources, and event stream until asynchronous disposal completes.</summary>
internal interface IUserSession : IAsyncDisposable
{
    string Id { get; }

    string ProviderId { get; }

    string CanonicalModel { get; }

    string Model { get; }

    IMode Mode { get; }

    CancellationToken Lifetime { get; }

    TimeProvider TimeProvider { get; }

    UserSessionResources Resources { get; }

    IDiagnosticLog Diagnostics { get; }

    IImageArtifactRepository Images { get; }

    IAgentRegistry Registry { get; }

    IRuntimeStatus Status { get; }

    IQuestionBroker Questions { get; }

    IPermissionBroker Permissions { get; }

    ISkillCatalog SkillCatalog { get; }

    void UpdateMode(string mode);

    IMode ResolveMode(string mode);

    void UpdateSelection(ResolvedModelSelection model);

    /// <summary>Updates the foreground model and mode without replacing its agent session.</summary>
    void Update(ResolvedModelSelection? model, IMode? mode);

    /// <summary>Streams current inventory and subsequent events until cancelled or the session closes.</summary>
    IAsyncEnumerable<Event> Listen(CancellationToken cancellationToken);

    /// <summary>Durably admits text for the foreground agent without waiting for its turn to finish.</summary>
    Task<Admission> SendText(string prompt, string messageId, Delivery delivery, CancellationToken cancellationToken);

    /// <summary>Durably admits content for the foreground agent without waiting for its turn to finish.</summary>
    Task<Admission> Send(IReadOnlyList<ConversationPart> parts, string messageId, Delivery delivery, CancellationToken cancellationToken);

    /// <summary>Stops the foreground turn without shutting down parent-owned children.</summary>
    Task Interrupt(CancellationToken cancellationToken);

    /// <summary>Reads the foreground agent's projected message history.</summary>
    IReadOnlyList<string> History();

    /// <summary>Captures currently active process, agent, and task-run work.</summary>
    IReadOnlyList<ActiveWorkObservation> ActiveWork();

    Task SetGoal(string goal, CancellationToken cancellationToken);

    void ClearGoal();

    Task Compact(ContextSize? targetContextSize, CancellationToken cancellationToken);
}
