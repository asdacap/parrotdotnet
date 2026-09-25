import { SessionPicker } from "@/components/SessionPicker"
import { SessionView } from "@/components/SessionView"
import { useRoute } from "@/lib/router"

export function App() {
  const route = useRoute()
  return route.page === "session" ? <SessionView key={route.userSessionId} userSessionId={route.userSessionId} /> : <SessionPicker />
}
