# AgentSession member audit

Final source audited: `src/Parrot.Core/Agent/AgentSession.cs` after the scoped-dependency refactor. `AgentSession` is internal and has no public members. The table below enumerates every remaining public or internal declaration exactly once. Tests do not justify any retained visibility.

## Final public/internal declarations

| Declaration | Classification | Production responsibility and concrete consumers |
|---|---|---|
| `internal sealed class AgentSession(...)` | Intrinsic owner | Owns durable admission, drain serialization, provider turns, tool execution, interruption, and terminal delivery for one agent. Constructed by `AgentSessionComposition`; direct construction is also used by test fixtures. Its private constructor values include `AgentQueues` for queue delivery and `AgentSessionParentScope` for terminal completion routing; neither dependency is exposed. |
| `internal string SessionId` | Intrinsic identity | Stable runtime address used by `AgentRegistry`, `ChildRegistry`, `AgentResolver`, `RuntimeStatus`, `RuntimeTreeStatusProvider`, `ShellProcessOwner`, and agent tools. |
| `internal string Name` | Intrinsic identity | Human-readable identity used by registry/status snapshots, child completion, process ownership, and spawn/send results. |
| `internal string ParentSessionId` | Intrinsic relationship metadata | Enforces and reports parent-child relationships in `AgentRegistry`, `ChildRegistry`, `AgentResolver`, `RuntimeStatus`, and process/status snapshots. |
| `internal string ParentSessionName` | Intrinsic relationship metadata | Resolves and reports the parent relationship in `AgentResolver`, `RuntimeStatus`, and `ShellProcessOwner`. |
| `internal int Depth` | Intrinsic hierarchy metadata | Enforces root/child registration and recursion constraints and configures child-facing tools in `AgentRegistry`, `AgentResolver`, `AgentSpawnTool`, and `QuestionToolFactory`. |
| `internal AgentIdentity Identity` | Unavoidable relationship access | Supplies immutable owner identity for scope/registry ownership validation in `AgentSessionDirectScope`, `AgentRegistry`, `ChildRegistry`, queue-owner snapshots in `AgentQueueCatalog`, and runtime-tree metadata in `RuntimeTreeStatusProvider`. |
| `internal ChildRegistry ChildRegistry` | Unavoidable relationship access | Represents this session's owned child relationship. `AgentRegistry` and `AgentSessionDirectScope` validate scope ownership through it; `AgentTaskGraphRunner`, `AgentStatusTool`, `ChildQuestionCoordinator`, and descendant resolution consume it. |
| `internal AgentSessionActivity Activity` | Unavoidable relationship access | Is the production observation surface for a resolved target's provider/tool/lifecycle activity in `AgentStatusTool`; the session records its own activity internally. |
| `internal AgentSelection Selection()` | Intrinsic selection observation | Returns the gate-protected immutable selection snapshot used by `AgentPolicyLineage` when deriving child policy and profile lineage. |
| `internal void UpdateSelection(ModelSelector selectedModel, IMode mode)` | Intrinsic selection mutation | Applies user-session model/mode changes at turn boundaries; called by `UserSession`. |
| `internal void Recover()` | Intrinsic lifecycle | Wakes durable pending input when `UserSession` initializes a recovered main agent. |
| `internal Task Interrupt(CancellationToken cancellationToken)` | Intrinsic lifecycle | Cancels and joins the owned drain while preserving pending input; called by `UserSession` and by `Abort`. |
| `internal Task Settled()` | Intrinsic lifecycle | Lets owning resources await drain completion before disposal; called by `UserSession` and `ChildRegistry`. |
| `internal Task Abort(CancellationToken cancellationToken)` | Intrinsic lifecycle | Permanently prevents further wakes and interrupts execution during agent-task cancellation; called by `AgentTaskGraphRunner`. |
| `internal void SetCheckpoint(string title, long assistantSequence, string toolCallId)` | Intrinsic conversation operation | Validates and records the current assistant tool-batch checkpoint; called by `SetCheckpointTool`. |
| `internal AgentSelection ResolvePolicySelection()` | Intrinsic authorization | Resolves this session's selected profile through its policy lineage; called by `AgentSendTool` for caller/recipient delegation checks. |
| `internal AgentPolicyLineage ResolvePolicyLineage()` | Intrinsic relationship policy | Supplies the parent lineage when `AgentSessionParentScope` links a child's authorization lineage. |
| `internal bool IsIdle()` | Intrinsic queue-delivery predicate | Lets `AgentQueues` admit notifications only while its owning session is idle. |
| `internal bool IsActive()` | Intrinsic active-work predicate | Supplies `AgentRegistry.Active` and `ActiveSnapshot` with execution/drain activity. |
| `internal bool IsWaitingForIncomingInput()` | Intrinsic queue-delivery predicate | Lets `AgentQueues` distinguish a wait-tool input wait from an ordinary idle session. |
| `internal Task<IncomingActivity?> WaitForIncomingInput(TimeSpan duration, TimeProvider timeProvider, CancellationToken cancellationToken)` | Intrinsic wait coordination | Coordinates the wait tool with pending input and queue/process/child wake activity; called by `WaitTool`. |
| `internal Task<(Admission Admission, bool FollowUp)> Send(IReadOnlyList<ConversationPart> parts, string messageId, Delivery delivery, CancellationToken cancellationToken)` | Intrinsic structured admission | Durably admits structured text/image input and reports follow-up ownership; `UserSession` delegates protocol ingress to it. |
| `internal Task<bool> ReceiveQueueNotification(QueueNotification notification, CancellationToken cancellationToken)` | Intrinsic queue admission | Converts a monitored queue item into an idempotent steer admission; called by `AgentQueues`. |
| `internal Task<AgentSendResult> Send(string message, CancellationToken cancellationToken)` | Intrinsic agent execution | Starts or steers agent-to-agent execution and returns its addressable execution identity; called by `AgentSendTool`, `AgentSpawnTool`, and `AgentTaskGraphRunner`. |
| `internal Task ReceiveChildQuestion(string message, string messageId, CancellationToken cancellationToken)` | Intrinsic relationship admission | Admits a child question and starts a parent follow-up when required; called by `ChildQuestionCoordinator`. |
| `internal Task ReceiveAgentCompletion(string name, string message, CancellationToken cancellationToken)` | Intrinsic relationship admission | Admits a child's terminal notification and starts the parent follow-up when required; called by `ChildRegistry`. |
| `internal Task ReceiveProcessCompletion(string name, string message, string messageId, CancellationToken cancellationToken)` | Intrinsic process admission | Admits idempotent shell-process completion and wakes the drain; called by `ManagedShellProcess`. |
| `internal Task<WaitAgentResult> Wait(int yieldAfterMilliseconds, CancellationToken cancellationToken)` | Intrinsic execution result | Maps a started execution into the agent-task terminal/yielded result contract; called by `AgentTaskGraphRunner`. |

## Removed proxies and private implementation

- `Queues` was removed. `AgentSession` uses its private `queues` constructor value for delivery; queue factories and tests retain `AgentQueues` directly.
- `Resolver` was removed, and `AgentResolver` was removed from the `AgentSession` constructor. Scoped composition constructs and injects the resolver directly into `AgentSendToolFactory` and `AgentStatusToolFactory`; resolver tests retain or directly construct the scoped resolver.
- `ApproveWrites` was removed. `PermissionBroker.PendingRequest` retains the exact requesting `AgentSessionSecurity` and approves that instance.
- `ParentScope` was removed. The private `parentScope` constructor value remains solely for terminal completion routing to the parent scope.
- `Model` was removed after an AgentSession-qualified search found no production consumer. `UserSession.Model` and protocol/store model properties are separate declarations.
- The drain state is the private `_state` field. Lifecycle tests observe `AgentSessionActivity`, settlement, durable events, or predicates instead of test-only state visibility.
- `Admit` and `ResolveScope` were removed. Tests use structured admission and retained `AgentIdentity.Scope` respectively.
- The text admission adapter was removed; the structured admission overload remains distinct from execution `Send`.
- Selection security capture, drain-result waiting, and provider-event translation remain private as `CaptureSelection`, `WaitForDrainResult`, and `TranslateProviderEvent`. Tests assert retained security, settlement/durable terminal events, and provider event publication.

`Identity`, `ChildRegistry`, and `Activity` are retained only because other sessions/scopes need their ownership or relationship observations. Own-session tool factories receive scoped dependencies directly and do not traverse these members as dependency proxies.
