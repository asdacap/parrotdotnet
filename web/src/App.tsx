import { SessionPicker } from "@/components/SessionPicker"
import { SessionView } from "@/components/SessionView"
import { useRoute } from "@/lib/router"
import { token } from "@/rpc/client"

export function App() {
  const route = useRoute()
  if (!token) {
    return (
      <main className="flex h-dvh items-center justify-center p-6 text-sm text-muted-foreground">
        Open the link printed by <code className="mx-1 font-mono">parrot web</code> to sign in.
      </main>
    )
  }
  return route.page === "session" ? <SessionView key={route.userSessionId} userSessionId={route.userSessionId} /> : <SessionPicker />
}
