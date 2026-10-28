# Waking an open Claude Code session

Each open Claude Code session is its own PoniExp agent, `claude.<first 8 of the session id>` (e.g. `ise/claude.72f659ef`).
A message sent to that address wakes that session and no other; `ise/claude` stays the MCP server's own agent.

Merge `settings.hooks.json` into `~/.claude/settings.json` (`poniexp-client` must be on the hooks' PATH, or use its full path).
No launch flag is needed: it uses plain command hooks with `asyncRewake`, not channels.

- **SessionStart**, `--announce`: tells the session its address as context.
- **SessionStart / Stop**, `asyncRewake`: holds the session agent's stream until a message it hasn't been nudged about arrives,
  then exits 2 with a short nudge (count, senders, which `agent` to pass; never the body). Claude wakes even when idle; a message
  that arrives mid-turn is shown as soon as the running tool call finishes. Each wake fires once, so the Stop hook re-arms it
  after every turn; a second waiter for the same session exits at once. Nudged ids live in `$TMPDIR/poniexp-hooks/`.
- **SessionEnd**: retires the session agent (later messages to it are rejected).
- The MCP messaging tools (`inbox`, `ack`, `reply`, `send`, `attach`, `fetch`, `release`, `conversation(s)`, `message`) take an
  optional `agent`, which may only be the server's own agent or one of its sessions (`claude.<id>`).

`--max-wait` must stay below the hook's `timeout` (Claude Code enforces `timeout` on `asyncRewake` hooks). After it, an idle
session stops listening until its next turn.
