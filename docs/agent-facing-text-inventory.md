# Agent-facing text inventory

This is a pre-migration inventory. It changes no prompt ownership. The exhaustive per-file classification is in `agent-facing-text-inventory.tsv`.

## Scope and boundary rules

A confirmed row contains repository-authored prose that is delivered through `LLMRequest.Instructions`, `LLMRequest.Messages`, `LLMRequest.Tools`, `AgentSession.Send`, injected system/history messages, or model-visible tool-result history. Template IDs are proposed stable ownership names, not implemented registrations. Dynamic user, filesystem, command, provider, and child-agent content is external data even when transported beside a template. Protocol tokens/schema covers identifiers, JSON/schema/property descriptions without an independently authored instruction, build metadata, and source with no confirmed delivery prose. UI-only text terminates at the CLI. Errors-that-cannot-reach-a-model covers test-only fixtures and assertions; production tool errors that enter tool history are confirmed instead.

`AGENTS.md` is confirmed because `AgentsPrompt` reads it; its existing working-tree modification was preserved. `README.md`, `CLAUDE.md`, and ordinary docs are external data because no automatic delivery path reads them.

## Counts

- git-tracked files inventoried: 840
- confirmed: 70
- errors-that-cannot-reach-a-model: 147
- external-data: 3
- protocol-tokens-schema: 443
- ui-only-text: 177

## Verification

Verify exact coverage with `git ls-files | sort` against TSV column 1 (excluding its header). Confirmed rows require nonempty delivery boundary and proposed template ID; excluded rows require a nonempty evidence reason.
