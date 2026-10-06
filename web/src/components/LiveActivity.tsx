import { Loader2Icon } from "lucide-react"
import { useEffect, useState } from "react"

import type { ActiveShellProcess, AgentTaskProgressSnapshot, QueueState } from "@/gen/parrot_pb"
import { TaskTree, fromProgress, isSettled } from "@/components/TaskTree"
import { formatDuration } from "@/lib/duration"
import type { OwnerInventory } from "@/session/inventory"

interface LiveActivityProps {
  queues: ReadonlyMap<string, OwnerInventory<QueueState>>
  processes: ReadonlyMap<string, OwnerInventory<ActiveShellProcess>>
  // The newest task graph per agent session.
  taskProgress: ReadonlyMap<string, AgentTaskProgressSnapshot>
}

// Unsettled task graphs, queues holding work and background shell processes, as the terminal CLI keeps them in its live area.
export function LiveActivity({ queues, processes, taskProgress }: LiveActivityProps) {
  // Milliseconds since the latest process snapshot, which each elapsed time is as of.
  const [sinceSnapshot, setSinceSnapshot] = useState(0)
  const running = [...processes.values()].flatMap((owner) => owner.items)
  const waiting = [...queues.values()].flatMap((owner) => owner.items).filter((queue) => queue.itemCount > 0)
  // A child agent's graph nests under its task in its owner's graph instead of standing alone.
  const nested = new Set([...taskProgress.values()].flatMap((tree) => tree.rootNodes.map((node) => node.agentSessionId)))
  const trees = [...taskProgress.entries()]
    .filter(([agentSessionId]) => !nested.has(agentSessionId))
    .map(([agentSessionId, tree]) => ({ agentSessionId, nodes: tree.rootNodes.map((node) => fromProgress(node, taskProgress)) }))
    .filter((tree) => !tree.nodes.every(isSettled))

  useEffect(() => {
    const snapshotAt = Date.now()
    const tick = () => { setSinceSnapshot(Date.now() - snapshotAt) }
    const reset = setTimeout(tick, 0)
    const timer = setInterval(tick, 1000)
    return () => {
      clearTimeout(reset)
      clearInterval(timer)
    }
  }, [processes])

  if (running.length === 0 && waiting.length === 0 && trees.length === 0) return null
  return (
    <ul className="mx-auto flex w-full max-w-4xl flex-col gap-0.5 px-4 pb-2 font-mono text-xs text-muted-foreground">
      {trees.map((tree) => (
        <li key={tree.agentSessionId}>
          <TaskTree title="Agent tasks:" nodes={tree.nodes} />
        </li>
      ))}
      {waiting.map((queue) => (
        <li key={`${queue.ownerAgentSessionId}/${queue.name}`}>
          queue: {queue.name} · {queue.itemCount} {queue.itemCount === 1 ? "item" : "items"}
          {queue.description && ` — ${queue.description}`}
          {queue.ownerAgentName && ` [${queue.ownerAgentName}]`}
        </li>
      ))}
      {running.map((process) => (
        <li key={process.processId} className="flex items-center gap-1">
          <Loader2Icon className="size-3 animate-spin" />$ {process.command} ({process.name} running{" "}
          {formatDuration(Number(process.elapsedMs) + sinceSnapshot)})
          {process.ownerAgentName && ` [${process.ownerAgentName}]`}
        </li>
      ))}
    </ul>
  )
}
