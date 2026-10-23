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
