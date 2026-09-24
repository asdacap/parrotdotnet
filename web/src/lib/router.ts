import { useSyncExternalStore } from "react"

export type Route = { page: "sessions" } | { page: "session"; userSessionId: string }

function subscribe(onChange: () => void) {
  window.addEventListener("hashchange", onChange)
  return () => { window.removeEventListener("hashchange", onChange) }
}

export function useRoute(): Route {
  const hash = useSyncExternalStore(subscribe, () => location.hash)
  const sessionMatch = /^#\/s\/(.+)$/.exec(hash)
  return sessionMatch?.[1] ? { page: "session", userSessionId: decodeURIComponent(sessionMatch[1]) } : { page: "sessions" }
}

export function navigateToSession(userSessionId: string) {
  location.hash = `#/s/${encodeURIComponent(userSessionId)}`
}
