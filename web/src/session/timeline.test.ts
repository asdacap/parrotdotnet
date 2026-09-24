import { create, type MessageInitShape } from "@bufbuild/protobuf"
import { describe, expect, it } from "vitest"

import { EventSchema } from "@/gen/parrot_pb"
import { emptyTimeline, reduceTimeline, type TimelineState } from "@/session/timeline"

type Payload = MessageInitShape<typeof EventSchema>["payload"]

const root = "root-agent"
const child = "child-agent"

function replay(events: [agentSessionId: string, payload: Payload][]): TimelineState {
  return events.reduce(
    (state, [agentSessionId, payload]) =>
      reduceTimeline(state, { type: "event", event: create(EventSchema, { agentSessionId, payload }) }),
    emptyTimeline,
  )
}

describe("reduceTimeline", () => {
  it.each<[string, [string, Payload][], Partial<TimelineState>]>([
    [
      "concatenates streamed text until another item from the same agent intervenes",
      [
        [root, { case: "textChunk", value: { fragment: "Hel" } }],
        [child, { case: "reasoningChunk", value: { fragment: "thinking" } }],
        [root, { case: "textChunk", value: { fragment: "lo" } }],
        [root, { case: "toolStarted", value: { toolCallId: "call-1", toolName: "read" } }],
        [root, { case: "textChunk", value: { fragment: "Done" } }],
      ],
      {
        items: [
          { kind: "assistant", agentSessionId: root, text: "Hello" },
          { kind: "reasoning", agentSessionId: child, text: "thinking" },
          { kind: "tool", agentSessionId: root, toolCallId: "call-1", name: "read", args: "", status: "running", output: "" },
          { kind: "assistant", agentSessionId: root, text: "Done" },
        ],
      },
    ],
    [
      "tracks a tool call from argument chunks to its result in a single card",
      [
        [root, { case: "toolCallChunk", value: { toolCallId: "call-1", toolName: "exec", argumentsFragment: '{"cmd":' } }],
        [root, { case: "toolCallChunk", value: { toolCallId: "call-1", argumentsFragment: '"ls"}' } }],
        [root, { case: "toolStarted", value: { toolCallId: "call-1", toolName: "exec" } }],
        [root, { case: "toolFinished", value: { toolCallId: "call-1", toolName: "exec", result: "a.txt" } }],
        [root, { case: "toolStarted", value: { toolCallId: "call-2", toolName: "write" } }],
        [root, { case: "toolError", value: { toolCallId: "call-2", toolName: "write", message: "denied" } }],
      ],
      {
        items: [
          { kind: "tool", agentSessionId: root, toolCallId: "call-1", name: "exec", args: '{"cmd":"ls"}', status: "finished", output: "a.txt" },
          { kind: "tool", agentSessionId: root, toolCallId: "call-2", name: "write", args: "", status: "error", output: "denied" },
        ],
      },
    ],
    [
      "ends a busy turn on failure",
      [
        [root, { case: "turnStarted", value: { model: "gpt" } }],
        [root, { case: "turnFailed", value: { message: "rate limited" } }],
      ],
      {
        busy: false,
        items: [
          { kind: "turn", agentSessionId: root, outcome: "started", detail: "gpt" },
          { kind: "turn", agentSessionId: root, outcome: "failed", detail: "rate limited" },
        ],
      },
    ],
    [
      "stays busy while the root turn runs, regardless of subagent turns",
      [
        [root, { case: "turnStarted", value: {} }],
        [child, { case: "agentStarted", value: { parentAgentSessionId: root, name: "helper" } }],
        [child, { case: "turnStarted", value: {} }],
        [child, { case: "turnEnded", value: { finishReason: "stop" } }],
      ],
      { busy: true },
    ],
    [
      "is idle once the root turn ends even while a subagent keeps working",
      [
        [child, { case: "agentStarted", value: { parentAgentSessionId: root, name: "helper" } }],
        [root, { case: "turnStarted", value: {} }],
        [child, { case: "turnStarted", value: {} }],
        [root, { case: "turnEnded", value: { finishReason: "stop" } }],
      ],
      { busy: false },
    ],
  ])("%s", (_name, events, expected) => {
    expect(replay(events)).toMatchObject(expected)
  })
})
