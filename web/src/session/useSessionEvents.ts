import { Code, ConnectError } from "@connectrpc/connect"
import { useEffect, useReducer, useState } from "react"

import { delay } from "@/lib/delay"
import { parrot } from "@/rpc/client"
import { emptyTimeline, reduceTimeline } from "@/session/timeline"

const initialRetryMilliseconds = 500
const maximumRetryMilliseconds = 10_000

export function useSessionEvents(userSessionId: string) {
  const [timeline, dispatch] = useReducer(reduceTimeline, emptyTimeline)
  const [connected, setConnected] = useState(false)
  // Bumped on (re)connect and on every permission event so the permission list reconciles.
  const [permissionRevision, setPermissionRevision] = useState(0)
  // Set when the session is no longer hosted anywhere this server can reach; retrying cannot help.
  const [lost, setLost] = useState("")

  useEffect(() => {
    const abort = new AbortController()
    // The first connection replays the whole transcript; a reconnection replays only what came after the last event seen.
    const seenEventIds = new Set<string>()
    let lastEventId = ""
    void (async () => {
      let retryMilliseconds = initialRetryMilliseconds
      while (!abort.signal.aborted) {
        try {
          const stream = parrot.listen({ userSessionId, replay: true, replayAfterEventId: lastEventId }, { signal: abort.signal })
          setPermissionRevision((revision) => revision + 1)
          for await (const event of stream) {
            setConnected(true)
            retryMilliseconds = initialRetryMilliseconds
            if (event.id) {
              if (seenEventIds.has(event.id)) continue
              seenEventIds.add(event.id)
              lastEventId = event.id
            }
            dispatch({ type: "event", event })
            if (event.payload.case === "permissionPending") setPermissionRevision((revision) => revision + 1)
          }
        } catch (error) {
          const failure = ConnectError.from(error)
          if (failure.code === Code.NotFound) {
            setLost(failure.message)
            return
          }
          // Otherwise reconnected below.
        }
        setConnected(false)
        await delay(retryMilliseconds, abort.signal)
        retryMilliseconds = Math.min(retryMilliseconds * 2, maximumRetryMilliseconds)
      }
    })()
    return () => { abort.abort() }
  }, [userSessionId])

  return { timeline, dispatch, connected, permissionRevision, lost }
}
