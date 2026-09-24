import { Code, ConnectError } from "@connectrpc/connect"
import { ArrowLeftIcon } from "lucide-react"
import { useEffect, useState } from "react"

import { Delivery, type UserSession } from "@/gen/parrot_pb"
import { Composer } from "@/components/Composer"
import { PermissionDialog } from "@/components/PermissionDialog"
import { QuestionPanel } from "@/components/QuestionPanel"
import { SlashDialogHost } from "@/components/SlashDialogHost"
import { StatusBar } from "@/components/StatusBar"
import { Timeline } from "@/components/Timeline"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { describeHost, parrot } from "@/rpc/client"
import { useSessionEvents } from "@/session/useSessionEvents"
import { useSlashRunner } from "@/slash/useSlashRunner"

async function openSession(userSessionId: string): Promise<UserSession> {
  const { workingDirectory } = await describeHost()
  try {
    return await parrot.resumeSession({ userSessionId, workingDirectory, interactivePermissions: true })
  } catch (error) {
    if (ConnectError.from(error).code !== Code.AlreadyExists) throw error
    return await parrot.attachSession({ userSessionId, workingDirectory })
  }
}

export function SessionView({ userSessionId }: { userSessionId: string }) {
  const [session, setSession] = useState<UserSession | null>(null)
  const [failure, setFailure] = useState("")

  useEffect(() => {
    openSession(userSessionId).then(setSession, (error: unknown) => { setFailure(ConnectError.from(error).message) })
  }, [userSessionId])

  return (
    <div className="flex h-dvh flex-col">
      <header className="flex items-center gap-3 border-b px-4 py-2 text-sm">
        <Button asChild variant="ghost" size="icon">
          <a href="#/" title="Sessions">
            <ArrowLeftIcon />
          </a>
        </Button>
        <span className="truncate font-mono">{userSessionId}</span>
        {session && <Badge variant="secondary">{session.model || "default model"}</Badge>}
        {session?.mode && <Badge variant="outline">{session.mode}</Badge>}
      </header>
      {session ? (
        <SessionChat userSessionId={session.id} />
      ) : (
        <div className="flex flex-1 items-center justify-center text-sm text-muted-foreground">
          {failure ? <span className="text-destructive">{failure}</span> : "Opening session…"}
        </div>
      )}
    </div>
  )
}

function SessionChat({ userSessionId }: { userSessionId: string }) {
  const { timeline, dispatch, connected, permissionRevision } = useSessionEvents(userSessionId)
  const slash = useSlashRunner(userSessionId, timeline.busy, dispatch)

  const reportFailure = (message: string) => { dispatch({ type: "error", message }) }

  function send(text: string) {
    if (text.startsWith("/")) {
      slash.submit(text)
      return
    }
    parrot
      .sendMessage({ userSessionId, text, messageId: crypto.randomUUID(), delivery: Delivery.STEER })
      .catch((error: unknown) => { reportFailure(ConnectError.from(error).message) })
  }

  function interrupt() {
    parrot.interrupt({ userSessionId }).catch((error: unknown) => { reportFailure(ConnectError.from(error).message) })
  }

  if (slash.exited) {
    return <div className="flex flex-1 items-center justify-center text-sm text-muted-foreground">The Parrot server stopped.</div>
  }

  return (
    <>
      <Timeline items={timeline.items} subagentIds={timeline.subagentIds} />
      {slash.queued.length > 0 && (
        <div className="mx-auto flex w-full max-w-4xl flex-wrap items-center gap-2 px-4 pb-2 text-xs text-muted-foreground">
          waiting for the turn to finish:
          {slash.queued.map((text, index) => (
            <Badge key={index} variant="outline">{text}</Badge>
          ))}
        </div>
      )}
      <QuestionPanel userSessionId={userSessionId} onFailure={reportFailure} />
      <SlashDialogHost frame={slash.prompt?.frame} loadingActivity={slash.loadingActivity} onAnswer={(answer) => void slash.answer(answer)} />
      <PermissionDialog userSessionId={userSessionId} revision={permissionRevision} onFailure={reportFailure} />
      <Composer busy={timeline.busy} onSend={send} onInterrupt={interrupt} />
      <StatusBar usage={timeline.usage} connected={connected} busy={timeline.busy} />
    </>
  )
}
