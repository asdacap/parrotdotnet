import type { Event, PlanCompleted, SessionUsageSnapshot } from "@/gen/parrot_pb"

export type ToolStatus = "running" | "finished" | "cancelled" | "error"

export type TimelineItem =
  | { kind: "user"; agentSessionId: string; text: string }
  | { kind: "assistant"; agentSessionId: string; text: string }
  | { kind: "reasoning"; agentSessionId: string; text: string }
  | {
      kind: "tool"
      agentSessionId: string
      toolCallId: string
      name: string
      args: string
      status: ToolStatus
      output: string
    }
  | { kind: "agent"; agentSessionId: string; name: string; outcome: "started" | "finished" | "failed"; detail: string }
  | { kind: "turn"; agentSessionId: string; outcome: "started" | "ended" | "failed"; detail: string }
  | { kind: "plan"; agentSessionId: string; markdown: string }
  | { kind: "print"; lines: string[] }
  | { kind: "error"; message: string }

type ToolItem = Extract<TimelineItem, { kind: "tool" }>

export interface TimelineState {
  items: TimelineItem[]
  subagentIds: ReadonlySet<string>
  busy: boolean
  usage: SessionUsageSnapshot | undefined
  // The main agent's plan still waiting for the user's decision; any later input settles it.
  pendingPlan: PlanCompleted | undefined
}

export type TimelineAction =
  | { type: "event"; event: Event }
  | { type: "print"; lines: string[] }
  | { type: "error"; message: string }
  | { type: "planSettled" }

export const emptyTimeline: TimelineState = {
  items: [],
  subagentIds: new Set(),
  busy: false,
  usage: undefined,
  pendingPlan: undefined,
}

function appendStreamed(state: TimelineState, event: Event, kind: "assistant" | "reasoning", fragment: string): TimelineState {
  const lastIndex = state.items.findLastIndex(
    (item) => "agentSessionId" in item && item.agentSessionId === event.agentSessionId,
  )
  const last = state.items[lastIndex]
  if (last?.kind === kind) {
    return { ...state, items: state.items.with(lastIndex, { ...last, text: last.text + fragment }) }
  }
  return { ...state, items: [...state.items, { kind, agentSessionId: event.agentSessionId, text: fragment }] }
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

function reduceEvent(state: TimelineState, event: Event): TimelineState {
  const { agentSessionId } = event
  const isRoot = !state.subagentIds.has(agentSessionId)
  const payload = event.payload
  switch (payload.case) {
    case "inputAdmitted":
      return append(
        { ...state, pendingPlan: isRoot ? undefined : state.pendingPlan },
        { kind: "user", agentSessionId, text: payload.value.content },
      )
    case "planCompleted":
      return append(
        { ...state, pendingPlan: isRoot && payload.value.dialog ? payload.value : state.pendingPlan },
        { kind: "plan", agentSessionId, markdown: payload.value.markdown },
      )
    case "textChunk":
      return appendStreamed(state, event, "assistant", payload.value.fragment)
    case "reasoningChunk":
      return appendStreamed(state, event, "reasoning", payload.value.fragment)
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
    case "agentStarted":
      return append(
        { ...state, subagentIds: new Set(state.subagentIds).add(agentSessionId) },
        { kind: "agent", agentSessionId, name: payload.value.name, outcome: "started", detail: "" },
      )
    case "agentFinished":
      return append(state, { kind: "agent", agentSessionId, name: payload.value.name, outcome: "finished", detail: "" })
    case "agentFailed":
      return append(state, {
        kind: "agent",
        agentSessionId,
        name: payload.value.name,
        outcome: "failed",
        detail: payload.value.message,
      })
    case "turnStarted":
      return append(
        { ...state, busy: state.busy || isRoot, pendingPlan: isRoot ? undefined : state.pendingPlan },
        { kind: "turn", agentSessionId, outcome: "started", detail: payload.value.model },
      )
    case "turnEnded":
      return append(
        { ...state, busy: state.busy && !isRoot },
        { kind: "turn", agentSessionId, outcome: "ended", detail: payload.value.finishReason },
      )
    case "turnFailed":
      return append(
        { ...state, busy: state.busy && !isRoot },
        { kind: "turn", agentSessionId, outcome: "failed", detail: payload.value.message },
      )
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
