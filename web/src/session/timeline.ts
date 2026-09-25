import {
  ReasoningKind,
  type ActiveShellProcess,
  type AgentTaskProgressSnapshot,
  type Event,
  type PlanCompleted,
  type PlanTaskDeclaration,
  type QueueState,
  type SessionUsageSnapshot,
} from "@/gen/parrot_pb"
import { formatDuration } from "@/lib/duration"
import { emptyInventory, observeInventory, type OwnerInventory } from "@/session/inventory"

export type ToolStatus = "running" | "finished" | "cancelled" | "error"
export type NoticeTone = "muted" | "active" | "success" | "error"

export type TimelineItem =
  | { kind: "user"; agentSessionId: string; inputId: string; text: string }
  | { kind: "assistant"; agentSessionId: string; text: string }
  | { kind: "reasoning"; agentSessionId: string; text: string; completed: boolean }
  | {
      kind: "tool"
      agentSessionId: string
      toolCallId: string
      name: string
      args: string
      status: ToolStatus
      output: string
    }
  | { kind: "notice"; agentSessionId: string; tone: NoticeTone; text: string; detail: string }
  | { kind: "plan"; agentSessionId: string; markdown: string; taskDeclarations: PlanTaskDeclaration[] }
  | { kind: "print"; lines: string[] }
  | { kind: "error"; message: string }

type ToolItem = Extract<TimelineItem, { kind: "tool" }>

export interface AgentInfo {
  name: string
  parentAgentSessionId: string
}

export interface TimelineState {
  items: TimelineItem[]
  // Every subagent seen; an agent session not in it is the main agent.
  agents: ReadonlyMap<string, AgentInfo>
  busy: boolean
  usage: SessionUsageSnapshot | undefined
  // The main agent's plan still waiting for the user's decision; any later input settles it.
  pendingPlan: PlanCompleted | undefined
  // The newest task tree per run_agent_tasks call.
  taskProgress: ReadonlyMap<string, AgentTaskProgressSnapshot>
  queues: ReadonlyMap<string, OwnerInventory<QueueState>>
  processes: ReadonlyMap<string, OwnerInventory<ActiveShellProcess>>
}

export type TimelineAction =
  | { type: "event"; event: Event }
  | { type: "print"; lines: string[] }
  | { type: "error"; message: string }
  | { type: "planSettled" }

export const emptyTimeline: TimelineState = {
  items: [],
  agents: new Map(),
  busy: false,
  usage: undefined,
  pendingPlan: undefined,
  taskProgress: new Map(),
  queues: new Map(),
  processes: new Map(),
}

// How deep an agent sits below the main agent; the main agent is 0.
export function agentDepth(agents: ReadonlyMap<string, AgentInfo>, agentSessionId: string): number {
  let depth = 0
  for (let current = agents.get(agentSessionId); current; current = agents.get(current.parentAgentSessionId)) {
    depth++
    if (depth > agents.size) break
  }
  return depth
}

function appendStreamed(state: TimelineState, event: Event, kind: "assistant" | "reasoning", fragment: string, completed = false): TimelineState {
  const lastIndex = state.items.findLastIndex(
    (item) => "agentSessionId" in item && item.agentSessionId === event.agentSessionId,
  )
  const last = state.items[lastIndex]
  if (last?.kind === "assistant" && kind === "assistant") {
    return { ...state, items: state.items.with(lastIndex, { ...last, text: last.text + fragment }) }
  }
  if (last?.kind === "reasoning" && kind === "reasoning" && !last.completed) {
    return { ...state, items: state.items.with(lastIndex, { ...last, text: last.text + fragment, completed }) }
  }
  const created: TimelineItem =
    kind === "assistant"
      ? { kind, agentSessionId: event.agentSessionId, text: fragment }
      : { kind, agentSessionId: event.agentSessionId, text: fragment, completed }
  return { ...state, items: [...state.items, created] }
}

function updateTool(
  state: TimelineState,
  event: Event,
  toolCallId: string,
  update: (tool: ToolItem) => Partial<ToolItem>,
): TimelineState {
  const index = state.items.findIndex((item) => item.kind === "tool" && item.toolCallId === toolCallId)
  const existing = state.items[index]
  if (existing?.kind === "tool") {
    return { ...state, items: state.items.with(index, { ...existing, ...update(existing) }) }
  }
  const created: ToolItem = {
    kind: "tool",
    agentSessionId: event.agentSessionId,
    toolCallId,
    name: "",
    args: "",
    status: "running",
    output: "",
  }
  return { ...state, items: [...state.items, { ...created, ...update(created) }] }
}

function append(state: TimelineState, item: TimelineItem): TimelineState {
  return { ...state, items: [...state.items, item] }
}

function notice(state: TimelineState, event: Event, text: string, tone: NoticeTone = "muted", detail = ""): TimelineState {
  return append(state, { kind: "notice", agentSessionId: event.agentSessionId, tone, text, detail })
}

function withInventory<T>(
  inventories: ReadonlyMap<string, OwnerInventory<T>>,
  owner: string,
  observe: (current: OwnerInventory<T>) => OwnerInventory<T>,
): ReadonlyMap<string, OwnerInventory<T>> {
  if (!owner) return inventories
  const current = inventories.get(owner) ?? emptyInventory
  const next = observe(current)
  return next === current ? inventories : new Map(inventories).set(owner, next)
}

function reduceEvent(state: TimelineState, event: Event): TimelineState {
  const { agentSessionId } = event
  const isRoot = !state.agents.has(agentSessionId)
  const payload = event.payload
  switch (payload.case) {
    case "inputAdmitted":
      return append(
        { ...state, pendingPlan: isRoot ? undefined : state.pendingPlan },
        { kind: "user", agentSessionId, inputId: payload.value.inputId, text: payload.value.content },
      )
    case "inputCanceled":
      return { ...state, items: state.items.filter((item) => item.kind !== "user" || item.inputId !== payload.value.inputId) }
    case "planCompleted":
      return append(
        { ...state, pendingPlan: isRoot && payload.value.dialog ? payload.value : state.pendingPlan },
        { kind: "plan", agentSessionId, markdown: payload.value.markdown, taskDeclarations: payload.value.taskDeclarations },
      )
    case "textChunk":
      return appendStreamed(state, event, "assistant", payload.value.fragment)
    case "reasoningChunk":
      // Raw reasoning is only a live "thinking" state, as in the terminal CLI; summaries stay in the transcript.
      return payload.value.kind === ReasoningKind.SUMMARY
        ? appendStreamed(state, event, "reasoning", payload.value.fragment, payload.value.completed)
        : state
    case "toolCallChunk": {
      const { toolCallId, toolName, argumentsFragment } = payload.value
      return updateTool(state, event, toolCallId, (tool) => ({ name: toolName || tool.name, args: tool.args + argumentsFragment }))
    }
    case "toolStarted":
      return updateTool(state, event, payload.value.toolCallId, () => ({ name: payload.value.toolName, status: "running" }))
    case "toolFinished":
      return updateTool(state, event, payload.value.toolCallId, () => ({ status: "finished", output: payload.value.result ?? "" }))
    case "toolCancelled":
      return updateTool(state, event, payload.value.toolCallId, () => ({ status: "cancelled" }))
    case "toolError":
      return updateTool(state, event, payload.value.toolCallId, () => ({ status: "error", output: payload.value.message }))
    case "toolRequestReceived":
      return payload.value.toolCallCount > 1 ? notice(state, event, `requested ${String(payload.value.toolCallCount)} tool calls`) : state
    case "agentStarted":
      return notice(
        {
          ...state,
          agents: new Map(state.agents).set(agentSessionId, {
            name: payload.value.name,
            parentAgentSessionId: payload.value.parentAgentSessionId,
          }),
        },
        event,
        `agent ${payload.value.name} started`,
        "active",
      )
    case "agentFinished":
      return notice(
        state,
        event,
        `agent ${payload.value.name} finished${payload.value.elapsedMs === undefined ? "" : ` (${formatDuration(Number(payload.value.elapsedMs))})`}`,
        "success",
      )
    case "agentFailed":
      return notice(state, event, `agent ${payload.value.name}: ${payload.value.message}`, "error")
    case "turnStarted":
      return { ...state, busy: state.busy || isRoot, pendingPlan: isRoot ? undefined : state.pendingPlan }
    case "turnEnded": {
      const ended = { ...state, busy: state.busy && !isRoot }
      if (!isRoot) return ended
      const { finishReason, inputTokens, outputTokens } = payload.value
      return finishReason === "interrupted"
        ? notice(ended, event, "agent interrupted", "error")
        : notice(ended, event, `${finishReason} - ${String(inputTokens)} total in / ${String(outputTokens)} total out`, "success")
    }
    case "turnFailed":
      return notice({ ...state, busy: state.busy && !isRoot }, event, payload.value.message, "error", payload.value.providerResponseBody)
    case "retryNotice":
      return notice(state, event, `retry ${String(payload.value.attempt)} in ${String(payload.value.retryAfterMs)} ms: ${payload.value.reason}`)
    case "compactionStarted":
      return notice(state, event, "compaction started", "active")
    case "compactionFinished":
      return notice(state, event, "compaction finished", "success")
    case "compactionFailed":
      return notice(state, event, `compaction failed: ${payload.value.message}`, "error")
    case "statusInjected":
      return notice(state, event, "↻ Status prompt injected")
    case "activeWorkReminderInjected":
      return isRoot ? notice(state, event, "↻ Active work reminder injected") : state
    case "exitReminderChanged":
      return isRoot
        ? notice(
            state,
            event,
            payload.value.state.case === "reminder" ? `↻ Exit reminder set: ${payload.value.state.value}` : "↻ Exit reminder cleared",
          )
        : state
    case "exitReminderInjected":
      return isRoot ? notice(state, event, "↻ Exit reminder injected") : state
    case "contextReminderInjected":
      return isRoot ? notice(state, event, `↻ Context reminder injected (${String(payload.value.usagePercent)}% context used)`) : state
    case "finalProviderRequestPromptInjected":
      return notice(state, event, "↻ Final provider request prompt injected")
    case "toolAvailabilityRestoredPromptInjected":
      return notice(state, event, "↻ Tool availability restored prompt injected")
    case "planValidationRepairInjected":
      return notice(state, event, `↻ Retrying after plan validation failure: ${payload.value.diagnostic}`)
    case "pendingChildQuestionReminderInjected":
      return notice(state, event, "↻ Retrying with pending child question reminder")
    case "skillLoaded":
      return isRoot ? notice(state, event, `↻ Skill loaded: ${payload.value.path}`) : state
    case "agentTaskProgressSnapshot": {
      const { originToolCallId, revision } = payload.value
      const current = state.taskProgress.get(originToolCallId)
      return current && current.revision >= revision
        ? state
        : { ...state, taskProgress: new Map(state.taskProgress).set(originToolCallId, payload.value) }
    }
    case "queueSnapshot": {
      const snapshot = payload.value
      return {
        ...state,
        queues: withInventory(state.queues, snapshot.ownerAgentSessionId, (owner) =>
          observeInventory(owner, {
            instance: snapshot.inventoryInstanceId,
            revision: snapshot.revision,
            index: snapshot.chunkIndex,
            final: snapshot.finalChunk,
            removed: snapshot.removed,
            shape: snapshot.rootAgentSessionId,
            items: snapshot.queues,
          }),
        ),
      }
    }
    case "shellProcessSnapshot": {
      const snapshot = payload.value
      if (snapshot.chunkCount === 0 || snapshot.chunkIndex >= snapshot.chunkCount) return state
      return {
        ...state,
        processes: withInventory(state.processes, snapshot.ownerAgentSessionId, (owner) =>
          observeInventory(owner, {
            instance: snapshot.inventoryInstanceId,
            revision: snapshot.revision,
            index: snapshot.chunkIndex,
            final: snapshot.chunkIndex + 1 === snapshot.chunkCount,
            removed: snapshot.removed,
            shape: String(snapshot.chunkCount),
            items: snapshot.processes,
          }),
        ),
      }
    }
    case "sessionUsageSnapshot":
      return { ...state, usage: payload.value }
    default:
      return state
  }
}

export function reduceTimeline(state: TimelineState, action: TimelineAction): TimelineState {
  switch (action.type) {
    case "event":
      return reduceEvent(state, action.event)
    case "print":
      return append(state, { kind: "print", lines: action.lines })
    case "error":
      return append(state, { kind: "error", message: action.message })
    case "planSettled":
      return { ...state, pendingPlan: undefined }
  }
}
