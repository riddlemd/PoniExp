# PoniExp Inbox (Hermes TUI widget)

Watch this machine's PoniExp conversations from inside the Hermes TUI.

- `/poniexp [since]`: modal browser, conversations → messages (default `7d`).
- `/poniexp-feed [since]`: small card in the top-right corner with the latest messages; clicking a
  message opens that conversation in `/poniexp`.

It reads `poniexp-client log --json --bodies` every 3 s while open, so it needs `poniexp-client` on
`PATH` and the daemon running. Nothing is sent.

## Install

Link it into Hermes' widget folder, so a `git pull` updates it, then run `/widgets-reload` in the TUI:

```bash
ln -s "$PWD/integrations/hermes/poniexp.mjs" ~/.hermes/tui-widgets/poniexp.mjs
```

On Windows, copy the file instead (`%USERPROFILE%\.hermes\tui-widgets\`), or use `mklink` from an
elevated prompt.

## Tests

```bash
node --test integrations/hermes/tests/poniexp.test.mjs
```
