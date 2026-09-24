import { SendIcon, SquareIcon } from "lucide-react"
import { useEffect, useState, type KeyboardEvent } from "react"

import type { SlashCommand } from "@/gen/web_pb"
import { Button } from "@/components/ui/button"
import { Command, CommandItem, CommandList } from "@/components/ui/command"
import { Textarea } from "@/components/ui/textarea"
import { parrotWeb } from "@/rpc/client"

interface ComposerProps {
  busy: boolean
  onSend: (text: string) => void
  onInterrupt: () => void
}

export function Composer({ busy, onSend, onInterrupt }: ComposerProps) {
  const [text, setText] = useState("")
  const [commands, setCommands] = useState<SlashCommand[]>([])
  const [highlighted, setHighlighted] = useState("")

  useEffect(() => {
    parrotWeb.listSlashCommands({}).then((response) => { setCommands(response.commands) }, () => undefined)
  }, [])

  const typingCommand = text.startsWith("/") && !/\s/.test(text)
  const matches = typingCommand ? commands.filter((command) => command.name.startsWith(text)) : []
  const selectedName = matches.some((command) => command.name === highlighted) ? highlighted : matches[0]?.name

  function submit(submitted: string) {
    if (!submitted.trim()) return
    onSend(submitted)
    setText("")
  }

  function moveHighlight(offset: number) {
    const index = matches.findIndex((command) => command.name === selectedName)
    const target = matches[(index + offset + matches.length) % matches.length]
    if (target) setHighlighted(target.name)
  }

  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (selectedName !== undefined && event.key === "ArrowDown") moveHighlight(1)
    else if (selectedName !== undefined && event.key === "ArrowUp") moveHighlight(-1)
    else if (selectedName !== undefined && event.key === "Tab") setText(`${selectedName} `)
    else if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) submit(selectedName ?? text)
    else return
    event.preventDefault()
  }

  return (
    <div className="relative mx-auto flex w-full max-w-4xl items-end gap-2 px-4 pb-3">
      {selectedName !== undefined && (
        <div className="absolute bottom-full left-4 w-96 rounded-md border shadow-md">
          <Command value={selectedName} onValueChange={setHighlighted} shouldFilter={false}>
            <CommandList>
              {matches.map((command) => (
                <CommandItem key={command.name} value={command.name} onSelect={submit}>
                  <span className="font-mono">{command.name}</span>
                  <span className="truncate text-xs text-muted-foreground">{command.summary}</span>
                </CommandItem>
              ))}
            </CommandList>
          </Command>
        </div>
      )}
      <Textarea
        autoFocus
        className="max-h-60 flex-1"
        placeholder="Message, or / for commands. Enter sends, Shift+Enter adds a line."
        value={text}
        onChange={(event) => { setText(event.target.value) }}
        onKeyDown={handleKeyDown}
      />
      {busy ? (
        <Button variant="destructive" size="icon" title="Stop" onClick={onInterrupt}>
          <SquareIcon />
        </Button>
      ) : (
        <Button size="icon" title="Send" onClick={() => { submit(text) }}>
          <SendIcon />
        </Button>
      )}
    </div>
  )
}
