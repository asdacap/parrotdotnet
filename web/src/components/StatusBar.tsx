import type { SessionUsageSnapshot } from "@/gen/parrot_pb"

const compact = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 })
const currency = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", maximumFractionDigits: 4 })

interface StatusBarProps {
  usage: SessionUsageSnapshot | undefined
  connected: boolean
  busy: boolean
}

export function StatusBar({ usage, connected, busy }: StatusBarProps) {
  const contextLimit = Number(usage?.contextLimit ?? 0n)
  const contextSize = Number(usage?.contextSize ?? 0n)
  return (
    <footer className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t px-4 py-1 font-mono text-xs text-muted-foreground">
      <span className={connected ? "text-foreground" : "text-destructive"}>{connected ? "connected" : "reconnecting…"}</span>
      <span>{busy ? "working" : "idle"}</span>
      {usage && (
        <>
          <span>in {compact.format(usage.inputTokens)} (cached {compact.format(usage.cachedInputTokens)})</span>
          <span>out {compact.format(usage.outputTokens)}</span>
          <span>
            context {compact.format(contextSize)}
            {contextLimit > 0 && ` / ${compact.format(contextLimit)} (${String(Math.round((contextSize / contextLimit) * 100))}%)`}
          </span>
          <span>{currency.format(usage.inputCost + usage.outputCost)}</span>
        </>
      )}
    </footer>
  )
}
