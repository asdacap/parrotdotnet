import { ConnectError } from "@connectrpc/connect"
import { useEffect, useState } from "react"

import { PermissionAction, PermissionTargetKind, type PendingPermission, type PermissionChoice } from "@/gen/parrot_pb"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { parrot } from "@/rpc/client"

interface PermissionDialogProps {
  userSessionId: string
  revision: number
  onFailure: (message: string) => void
}

export function PermissionDialog({ userSessionId, revision, onFailure }: PermissionDialogProps) {
  const [pending, setPending] = useState<PendingPermission[]>([])
  const [reconciled, setReconciled] = useState(0)
  const [reason, setReason] = useState("")

  useEffect(() => {
    let current = true
    parrot.listPendingPermissions({ userSessionId }).then(
      (response) => { if (current) setPending(response.permissions) },
      () => undefined,
    )
    return () => { current = false }
  }, [userSessionId, revision, reconciled])

  const permission = pending[0]
  if (!permission) return null

  async function reply(choice: PermissionChoice) {
    if (!permission) return
    try {
      await parrot.replyPermission({
        userSessionId,
        permissionRequestId: permission.id,
        choiceValue: choice.value,
        reason: choice.requiresReason ? reason : "",
      })
    } catch (error) {
      onFailure(ConnectError.from(error).message)
    }
    setReason("")
    setReconciled((count) => count + 1)
  }

  return (
    <Dialog open>
      <DialogContent onEscapeKeyDown={(event) => { event.preventDefault() }} onInteractOutside={(event) => { event.preventDefault() }}>
        <DialogHeader>
          <DialogTitle>Permission requested</DialogTitle>
          <DialogDescription>{permission.reason}</DialogDescription>
        </DialogHeader>
        <ul className="flex flex-col gap-1 font-mono text-xs">
          {permission.targets.map((target) => (
            <li key={target.path} className="truncate">
              {target.kind === PermissionTargetKind.DIRECTORY ? "dir " : "file "}
              {target.path}
            </li>
          ))}
        </ul>
        {permission.choices.some((choice) => choice.requiresReason) && (
          <Input placeholder="Reason" value={reason} onChange={(event) => { setReason(event.target.value) }} />
        )}
        <DialogFooter>
          {permission.choices.map((choice) => (
            <Button
              key={choice.value}
              variant={choice.action === PermissionAction.DENY ? "outline" : "default"}
              disabled={choice.requiresReason && !reason.trim()}
              onClick={() => void reply(choice)}
            >
              {choice.label}
            </Button>
          ))}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
