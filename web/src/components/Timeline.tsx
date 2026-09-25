import { useEffect, useRef } from "react"

import { Markdown } from "@/components/Markdown"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"
import type { TimelineItem, ToolStatus } from "@/session/timeline"

const toolBadgeVariants = {
  running: "secondary",
  finished: "outline",
  cancelled: "outline",
  error: "destructive",
} as const satisfies Record<ToolStatus, string>

function brief(text: string, limit: number) {
  const singleLine = text.replace(/\s+/g, " ").trim()
  return singleLine.length > limit ? `${singleLine.slice(0, limit)}…` : singleLine
}

function TimelineEntry({ item }: { item: TimelineItem }) {
  switch (item.kind) {
    case "user":
      return <div className="self-end rounded-lg bg-secondary px-3 py-2 text-sm whitespace-pre-wrap">{item.text}</div>
    case "assistant":
      return <div className="text-sm"><Markdown text={item.text} /></div>
    case "reasoning":
      return (
        <details className="text-xs text-muted-foreground">
          <summary className="cursor-pointer select-none">Reasoning</summary>
          <p className="mt-1 whitespace-pre-wrap">{item.text}</p>
        </details>
      )
    case "tool":
      return (
        <details className="rounded-md border px-3 py-1.5 text-xs">
          <summary className="flex cursor-pointer items-center gap-2 select-none">
            <span className="font-mono font-medium">{item.name}</span>
            <span className="min-w-0 flex-1 truncate font-mono text-muted-foreground">{brief(item.args, 160)}</span>
            <Badge variant={toolBadgeVariants[item.status]}>{item.status}</Badge>
          </summary>
          {item.args && <pre className="mt-2 overflow-x-auto font-mono whitespace-pre-wrap text-muted-foreground">{item.args}</pre>}
          {item.output && <pre className="mt-2 max-h-80 overflow-auto font-mono whitespace-pre-wrap">{item.output}</pre>}
        </details>
      )
    case "agent":
      return (
        <div className={cn("text-xs", item.outcome === "failed" ? "text-destructive" : "text-muted-foreground")}>
          agent {item.name} {item.outcome}
          {item.detail && `: ${item.detail}`}
        </div>
      )
    case "turn":
      return (
        <div className={cn("text-xs", item.outcome === "failed" ? "text-destructive" : "text-muted-foreground")}>
          turn {item.outcome}
          {item.detail && ` · ${item.detail}`}
        </div>
      )
    case "plan":
      return (
        <div className="rounded-md border bg-card px-3 py-2 text-sm">
          <div className="mb-1 text-xs font-medium text-muted-foreground uppercase">Plan</div>
          <Markdown text={item.markdown} />
        </div>
      )
    case "print":
      return <pre className="font-mono text-xs whitespace-pre-wrap">{item.lines.join("\n")}</pre>
    case "error":
      return <div className="text-xs text-destructive">{item.message}</div>
  }
}

export function Timeline({ items, subagentIds }: { items: TimelineItem[]; subagentIds: ReadonlySet<string> }) {
  const scrollerRef = useRef<HTMLDivElement>(null)
  const stickToBottom = useRef(true)

  useEffect(() => {
    const scroller = scrollerRef.current
    if (scroller && stickToBottom.current) scroller.scrollTop = scroller.scrollHeight
  }, [items])

  return (
    <div
      ref={scrollerRef}
      className="min-h-0 flex-1 overflow-y-auto"
      onScroll={(event) => {
        const scroller = event.currentTarget
        stickToBottom.current = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 48
      }}
    >
      <div className="mx-auto flex max-w-4xl flex-col gap-2 px-4 py-4">
        {items.map((item, index) => (
          <div
            key={index}
            className={cn("flex flex-col", "agentSessionId" in item && subagentIds.has(item.agentSessionId) && "border-l pl-4")}
          >
            <TimelineEntry item={item} />
          </div>
        ))}
      </div>
    </div>
  )
}
