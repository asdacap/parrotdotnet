import { ConnectError } from "@connectrpc/connect"
import { useEffect, useState } from "react"

import type { PendingQuestion } from "@/gen/parrot_pb"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { parrot } from "@/rpc/client"

const pollMilliseconds = 250

interface QuestionPanelProps {
  userSessionId: string
  onFailure: (message: string) => void
}

export function QuestionPanel({ userSessionId, onFailure }: QuestionPanelProps) {
  const [pendingQuestions, setPendingQuestions] = useState<PendingQuestion[]>([])

  useEffect(() => {
    let polling = false
    const timer = setInterval(() => {
      if (polling) return
      polling = true
      parrot
        .listPendingQuestions({ userSessionId })
        .then((response) => { setPendingQuestions(response.questions) }, () => undefined)
        .finally(() => { polling = false })
    }, pollMilliseconds)
    return () => { clearInterval(timer) }
  }, [userSessionId])

  return pendingQuestions.map((pending) => (
    <PendingQuestionCard key={pending.id} userSessionId={userSessionId} pending={pending} onFailure={onFailure} />
  ))
}

interface Draft {
  selected: string[]
  custom: string
}

function PendingQuestionCard({ userSessionId, pending, onFailure }: QuestionPanelProps & { pending: PendingQuestion }) {
  const [drafts, setDrafts] = useState<Draft[]>(() => pending.questions.map(() => ({ selected: [], custom: "" })))
  const [submitting, setSubmitting] = useState(false)

  function updateDraft(questionIndex: number, update: (draft: Draft) => Draft) {
    setDrafts((current) => current.map((draft, index) => (index === questionIndex ? update(draft) : draft)))
  }

  async function settle(send: () => Promise<unknown>) {
    setSubmitting(true)
    try {
      await send()
    } catch (error) {
      onFailure(ConnectError.from(error).message)
      setSubmitting(false)
    }
  }

  const answers = drafts.map((draft) => [...draft.selected, draft.custom.trim()].filter(Boolean).join(", "))
  const remainingSeconds = pending.remainingTimeoutMs === undefined ? undefined : Math.ceil(Number(pending.remainingTimeoutMs) / 1000)

  return (
    <section className="mx-auto flex w-full max-w-4xl flex-col gap-3 px-4 pb-3">
      <div className="flex flex-col gap-3 rounded-lg border bg-card p-4 text-sm">
        {pending.questions.map((question, questionIndex) => (
          <div key={questionIndex} className="flex flex-col gap-2">
            {question.header && <div className="text-xs font-medium text-muted-foreground uppercase">{question.header}</div>}
            <div className="whitespace-pre-wrap">{question.prompt}</div>
            <div className="flex flex-wrap gap-2">
              {question.options.map((option) => {
                const chosen = drafts[questionIndex]?.selected.includes(option.label) ?? false
                return (
                  <Button
                    key={option.label}
                    size="sm"
                    variant={chosen ? "default" : "outline"}
                    title={option.description}
                    onClick={() => {
                      updateDraft(questionIndex, (draft) => ({
                        ...draft,
                        selected: chosen
                          ? draft.selected.filter((label) => label !== option.label)
                          : question.multiple ? [...draft.selected, option.label] : [option.label],
                      }))
                    }}
                  >
                    {option.label}
                  </Button>
                )
              })}
            </div>
            {question.custom && (
              <Input
                placeholder="Custom answer"
                value={drafts[questionIndex]?.custom ?? ""}
                onChange={(event) => { updateDraft(questionIndex, (draft) => ({ ...draft, custom: event.target.value })) }}
              />
            )}
          </div>
        ))}
        <div className="flex items-center gap-2">
          {remainingSeconds !== undefined && <span className="text-xs text-muted-foreground">{remainingSeconds}s left</span>}
          <Button
            className="ml-auto"
            variant="outline"
            size="sm"
            disabled={submitting}
            onClick={() => void settle(() => parrot.rejectQuestion({ userSessionId, questionRequestId: pending.id }))}
          >
            Reject
          </Button>
          <Button
            size="sm"
            disabled={submitting || answers.some((answer) => !answer)}
            onClick={() =>
              void settle(() =>
                parrot.replyQuestion({ userSessionId, questionRequestId: pending.id, answers: answers.map((text) => ({ text })) }),
              )
            }
          >
            Answer
          </Button>
        </div>
      </div>
    </section>
  )
}
