# PoniExp Inbox (Hermes TUI widget)

Watch this machine's PoniExp conversations from inside the Hermes TUI.

- `/poniexp [since]`: modal browser, conversations → messages (default `7d`).
- `/poniexp-feed [since]`: small card in the top-right corner with the latest messages; clicking a
  message opens that conversation in `/poniexp`. `↻` marks conversations a Hermes session is
  working on right now. The `×` in its top-right corner closes it (needs Hermes' `dismissWidget`
  SDK call; without it the `×` is hidden and `/poniexp-feed` toggles the card off).
- `/poniexp-session <id>`: one Hermes session's transcript, live.

**Sessions.** Under each message, the conversation view shows the Hermes session that took it
(`ack`), replied with it or sent it: a gateway wake turn, a subagent or a chat. `s` picks one, Enter
or a click opens its transcript (prompts, replies, tool calls and the first line of each result),
and Esc goes back. A session view lists its subagents the same way. In the conversation list, `g`
lists every PoniExp wake turn and linked session, running ones marked `↻`.

It reads `poniexp-client log --json --bodies` and Hermes' `state.db` (read-only) every 3 s while
open, so it needs `poniexp-client` on `PATH`, the daemon running, and Node 22.5+ for `node:sqlite`
(Hermes ships Node 26). Without `state.db` it still shows the conversations. Nothing is sent.

The link comes from the session's own PoniExp tool results, so a wake turn shows once it has acked
something. `state.db` is Hermes' internal store: all the code that knows its layout is in the
"Hermes sessions" section of `poniexp.mjs`, and `tests/sessions.test.mjs` builds a copy of the
columns it uses.

## Install

Link it into Hermes' widget folder, so a `git pull` updates it, then run `/widgets-reload` in the TUI:

```bash
ln -s "$PWD/integrations/hermes/poniexp.mjs" ~/.hermes/tui-widgets/poniexp.mjs
```

On Windows, copy the file instead (`%USERPROFILE%\.hermes\tui-widgets\`), or use `mklink` from an
elevated prompt.

## Tests

```bash
node --test integrations/hermes/tests/*.test.mjs
```

# Waking an open Hermes session (plugin `poniexp-wake`)

Each interactive Hermes session (CLI, TUI, desktop) is its own PoniExp agent, `hermes-default.<id>`: the session id split at
punctuation, leading `local`/timestamp parts skipped, first 8 characters of the rest (`local-3f5ede2f…` → `hermes-default.3f5ede2f`).
A message to it is injected into that session as a short nudge (never the body) with `inject_message`: an idle session starts a
turn, a busy one runs it right after its current turn. Gateway sessions are left alone; `hermes-default` keeps its A2A wake.

- Registers on the session's first turn (`on_session_start`/`pre_llm_call`; Hermes has no earlier per-session hook), and tells the
  model its address on that turn. A session nobody has typed into isn't reachable yet.
- One `poniexp-client listen --agent <session agent> --json` child per session; `on_session_finalize` stops it and retires the agent.
- When the TUI is hosted by the gateway process, the session is found by the gateway's route key (`agent:main:local:…`), captured
  while the session's own hook runs; a standalone TUI backend matches the session id.
- The model reads and answers with the poniexp MCP tools' `agent` argument, so the MCP server must be a `poniexp-client` that has it.

Install (then restart the gateway: hooks it already loaded stay until a restart, and editing the file doesn't reload it):

```sh
ln -s "$PWD/integrations/hermes/plugin/poniexp-wake" ~/.hermes/plugins/poniexp-wake
hermes plugins enable poniexp-wake
hermes config set plugins.entries.poniexp-wake.allow_gateway_injection true
```

Settings: `PONIEXP_CLIENT` (binary path) and `PONIEXP_BASE_AGENT` (default `hermes-default`). Tests:
`python -m pytest integrations/hermes/plugin/tests` (a fake `poniexp-client`; no daemon or Hermes needed).
