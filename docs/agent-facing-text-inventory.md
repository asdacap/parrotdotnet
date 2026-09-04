# Agent-facing text inventory

This is the maintained agent-facing text inventory. It changes no prompt ownership. The exhaustive per-file classification is in `agent-facing-text-inventory.tsv`.

## Scope and boundary rules

A confirmed row contains repository-authored prose that is delivered through `LLMRequest.Instructions`, `LLMRequest.Messages`, `LLMRequest.Tools`, `AgentSession.Send`, injected system/history messages, or model-visible tool-result history. Template IDs are proposed stable ownership names, not implemented registrations. Dynamic user, filesystem, command, provider, and child-agent content is external data even when transported beside a template. Caller-only `compact_context` results are rendered from `compact-context-tool.*` templates and become model-visible tool-result history. Durable context reminders are rendered by `agent-session.context-reminder`, persisted as system history before their payload-free lifecycle event is published, and delivered through `LLMRequest.Messages`. Protocol tokens/schema covers identifiers, JSON/schema/property descriptions without an independently authored instruction, build metadata, and source with no confirmed delivery prose. UI-only text terminates at the CLI. Errors-that-cannot-reach-a-model covers test-only fixtures and assertions; production tool errors that enter tool history are confirmed instead.

`AGENTS.md` is confirmed because `AgentsPrompt` reads it; its existing working-tree modification was preserved. `README.md`, `CLAUDE.md`, and ordinary docs are external data because no automatic delivery path reads them.

## Context and compact-context coverage

Context status is model-facing request text, not merely terminal usage: its
estimated percentage is floor-rounded from the complete request (instructions,
profile-filtered tools, and effective history), capped at 100%, and reports an
unavailable percentage when the model has no positive context limit. The
`status.context` and `status.context-unavailable` templates document the
configured limit, fixed 5% cadence, and strict-above automatic trigger.
`agent-session.context-reminder` is durable system history; SQLite stores the
acknowledged band/model/window/history checkpoint before publishing the empty
`ContextReminderInjected` lifecycle event. Restart reads that checkpoint from
SQLite, while JSONL is only a refreshed projection. Candidate reminders are
coalesced to the highest crossed band and are compacted/recomputed or skipped
when insertion would exceed the window or strict trigger.

`compact_context` is a caller-only, non-parallel tool with a strict empty-object
schema. Its in-drain operation awaits the compaction core without self-queueing,
preserves the incomplete assistant/tool group, and returns model-visible
reduced, no-op, or unavailable text. This is separate from root-only `/compact`,
which waits for idle work and sends no model prompt. The TSV rows identify the
source files and every configured template/tool/result/history delivery boundary.

## Counts

- git-tracked files inventoried: 840
- confirmed: 79
- errors-that-cannot-reach-a-model: 154
- external-data: 6
- protocol-tokens-schema: 457
- ui-only-text: 178

## Verification

Verify exact coverage with `git ls-files | sort` against TSV column 1 (excluding its header). Confirmed rows require nonempty delivery boundary and proposed template ID; excluded rows require a nonempty evidence reason.
