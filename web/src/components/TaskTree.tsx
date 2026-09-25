import { AgentTaskProgressStatus, type AgentTaskProgressNode, type PlanTaskDeclaration } from "@/gen/parrot_pb"

// The terminal CLI's task icons (AgentTaskProgressFormatter).
const icons: Record<AgentTaskProgressStatus, string> = {
  [AgentTaskProgressStatus.UNSPECIFIED]: "?",
  [AgentTaskProgressStatus.PENDING]: "○",
  [AgentTaskProgressStatus.RUNNING]: "◐",
  [AgentTaskProgressStatus.SUCCEEDED]: "✓",
  [AgentTaskProgressStatus.FAILED]: "✗",
  [AgentTaskProgressStatus.BLOCKED]: "⊘",
  [AgentTaskProgressStatus.CANCELED]: "■",
}

interface TaskNode {
  name: string
  status: AgentTaskProgressStatus
  description: string
  detail: string
  children: TaskNode[]
}

export const fromProgress = (node: AgentTaskProgressNode): TaskNode => ({
  name: node.name,
  status: node.status,
  description: node.description,
  detail: "",
  children: node.children.map(fromProgress),
})

export const fromDeclaration = (task: PlanTaskDeclaration): TaskNode => ({
  name: task.name,
  status: task.status,
  description: task.description,
  detail: [
    task.dependencies.length > 0 ? `after ${task.dependencies.join(", ")}` : "",
    task.model ? `model ${task.model}` : "",
    task.acceptanceCriteria ? `done when ${task.acceptanceCriteria}` : "",
  ]
    .filter(Boolean)
    .join(" · "),
  children: task.payload.case === "children" ? task.payload.value.tasks.map(fromDeclaration) : [],
})

export function TaskTree({ title, nodes }: { title: string; nodes: TaskNode[] }) {
  return (
    <div className="font-mono text-xs">
      <div className="text-muted-foreground">{title}</div>
      <TaskList nodes={nodes} />
    </div>
  )
}

function TaskList({ nodes }: { nodes: TaskNode[] }) {
  return (
    <ul className="border-l pl-3">
      {nodes.map((node, index) => (
        <li key={index}>
          <span>{icons[node.status]} </span>
          <span>{node.description || node.name}</span>
          {node.detail && <span className="text-muted-foreground"> · {node.detail}</span>}
          {node.children.length > 0 && <TaskList nodes={node.children} />}
        </li>
      ))}
    </ul>
  )
}
