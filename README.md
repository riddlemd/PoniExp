# PoniExp

A secure relay for agents on several machines. Every machine keeps one outbound connection to a
router (SignalR over TLS), so the router always knows who's online, and no machine ever has to reach
another directly: laptops behind VPNs, hotel Wi-Fi or NAT just connect out.

```
 ise ──────┐                      ┌───── blinky
           │  wss:// (TLS, pinned) │
           ▼                      ▼
        router (Linux container on wojtek)
          · knows each machine by its public key
          · approved / pending / rejected list
          · routes signed, encrypted messages, queues them for machines that are offline
          · tells everyone who comes and goes
```

On each machine a **daemon** owns that one connection. Agents talk to their machine's daemon (an MCP
server, the CLI, or the local API) as **agents**: `machine/agent`, e.g. `wojtek/hermes-default`.

## How trust works

- **A machine is its key.** Each machine makes two key pairs on first use: an identity key (ECDSA
  P-256, which it signs with) and an encryption key (ECDH P-256, signed by the identity key). The
  private keys never leave it and are kept in the OS's secret store: DPAPI on Windows, owner-only
  files on macOS and Linux (the macOS keychain with `"Secrets": { "Store": "keychain" }` in
  settings.json, once builds are Developer ID signed; see below). The machine id is a hash of the
  identity public key.
- **Joining.** A machine sends its public key, name and description (OS, architecture, host name,
  notes), signed with that key. The router lists it as **pending** and shows a **join code**, which is
  the start of the machine id. The joining machine prints the same code. You compare the two, then
  approve.
- **Connecting.** On every connect the router sends a random challenge, and the machine signs it.
  Only a connection that proves an approved key can send, receive, or see the machine list.
- **Messages are signed end to end.** The sender signs every field of each message. The router checks
  the signature before routing, and the recipient checks it again against the sender's key. The
  recipient also judges expiry by its own clock and drops duplicates, so a compromised router could
  drop or delay messages, but not forge, alter, replay or stretch them.
- **The router's own identity.** The router makes a self-signed TLS certificate on first start and
  keeps it in its data volume. Clients pin its SHA-256, so it needs no public CA or domain name, and a
  man in the middle is refused.
- **Messages are encrypted end to end.** Everything but what routing needs (sender and recipient
  machine, expiry, sequence number, size) is encrypted to the recipient, and to the sender's own
  machine so it can read what it sent. Before encrypting, a machine checks that the recipient's
  encryption key is signed by the recipient's identity key, so the router can't hand out its own.
  The router sees who talks to whom, when and how much, not what or which agents.

## Run the router (Wojtek)

```sh
docker compose up -d --build
docker logs poniexp-server            # prints the certificate SHA-256 to pin
docker exec poniexp-server poniexp admin certificate
```

It listens on port 5443. Machines away from home need that port reachable, through the VPN (or, once
you're happy with the limits below, a router port forward). `docker ps` shows it as `healthy` when it
works (`poniexp health`).

**Limits.** A connection has 10 s to prove an approved or pending key, and at most 20 may be waiting
at once. A machine may hold 5 connections, and the router accepts 10 new join requests an hour.
Per-address limits apply on top, except to `PoniExp__SharedAddresses__*`: behind Rancher Desktop
every client arrives from its gateway (`192.168.127.1`, set in `compose.yaml`). Join requests expire
after 24 hours. Files: 2 GB per file, 10 GB per sender, 5 GB waiting per recipient, 50 GB in all
(and 5 GB of free disk kept), deleted when fetched by all, cancelled, or after 7 days
(`PoniExp__Blobs__*`, with `Overrides__<machine>__PerRecipientBytes` per machine). An offline
machine's queued messages are capped at 100 MB. Every limit is a `PoniExp__*` setting.

**Backups.** The TLS certificate, the approved machines, the owner key and the job board's unfinished jobs are
worth keeping (queued messages are short-lived; posters' daemons also re-post their open jobs to a
router that lost them). The file holds the certificate's private key: keep it somewhere safe, such as a
Bitwarden attachment.

```sh
docker exec poniexp-server poniexp admin backup - > poniexp-backup.json
docker exec -i poniexp-server poniexp admin restore - < poniexp-backup.json
docker restart poniexp-server            # if the restored certificate differs from the one in use
```

## Set up a machine

Build the client: `scripts/publish.sh <rid>` makes a self-contained single file (no .NET needed) in
`artifacts/<rid>/`, for `win-x64`, `osx-arm64`, `linux-x64`, etc.

```sh
poniexp-client init https://<router-host>:5443 --pin <sha256>   # creates this machine's keys
poniexp-client service install          # start the daemon at logon (as you); or run 'poniexp-client daemon'
poniexp-client join ise --notes "MacBook Pro"
#   asked to join as 'ise'. On the router, check this join code and approve:
#       3f2a-91c0-...
poniexp-client doctor                   # router, certificate pin, clock, membership, daemon
```

On the router:

```sh
docker exec poniexp-server poniexp admin list
docker exec poniexp-server poniexp admin approve 3f2a91c0     # id prefix or name
docker exec poniexp-server poniexp admin reject <id|name>
docker exec poniexp-server poniexp admin remove <id|name>     # forget it; it may ask again
docker exec poniexp-server poniexp admin clear-pending         # forget every waiting join request
docker exec poniexp-server poniexp admin blobs [clear --for <name>]   # files on the router; free a machine's share
docker exec poniexp-server poniexp admin group set <name> <address>... # groups: groups | group remove <name>
docker exec poniexp-server poniexp admin rooms [--collapsed]          # live rooms, or collapsed rooms' statistics
docker exec poniexp-server poniexp admin jobs [--all]                 # the job board (finished jobs too with --all)
docker exec poniexp-server poniexp admin owner [set "<ssh public key>"]  # the owner key and the approval desk
docker exec poniexp-server poniexp admin job remove <id>              # take a job off the board
docker exec poniexp-server poniexp admin traffic-log on        # record traffic (off by default; kept 30 days)
docker exec poniexp-server poniexp admin traffic-log show --since 1h [--machine <name>]
```

Approval reaches the waiting `join` at once.

**Starting the daemon automatically.** `service install` starts it at logon, as you: the `Run`
registry key on Windows (no admin needed, no console window), a LaunchAgent on macOS, a systemd user
unit on Linux. `service install --system` starts it at boot instead, so the machine receives while
nobody is logged in: a Windows service under its own account `NT SERVICE\PoniExp` (admin; it has its
own keys, so join it with `poniexp-client join` afterwards), a LaunchDaemon on macOS (sudo), a
lingering systemd user unit on Linux. `--dry-run` shows what it would do; `service uninstall
[--system]` undoes it; `service status` shows what's installed and running.

**Updating the client.** Pull, build for this machine's runtime only (with no argument
`publish.sh` builds all six, about 115 MB each), then run the installed client with the new build's path:

```bash
git pull --ff-only
scripts/publish.sh osx-arm64                                # this machine's rid: win-x64, linux-x64, ...
poniexp-client update artifacts/osx-arm64/poniexp-client    # add --system for a boot-time service (sudo / admin)
poniexp-client version && poniexp-client doctor
```

It checks the new binary runs here (`<new-binary> version`), stops the service, swaps the binary the
service runs (the old one is renamed aside, so it works on Windows where a running exe can't be
overwritten), starts the service and waits for the daemon to answer with the new version. If the
service won't start, or the daemon doesn't answer with that version within 30 seconds, the old binary
goes back and is started again. A missing or unrunnable new binary is refused before anything stops.
On Windows the renamed-aside `poniexp-client.exe.old` can stay behind while something still runs it;
the next `update` removes it.
`poniexp-client version` prints a build's version: `<version>+<git commit>`.

- **A client from before `update`** has no `version` command. Update that one by hand once:
  `service uninstall`, move the old binary aside (`mv poniexp-client.exe poniexp-client.exe.old`; on Windows
  the agents' MCP servers keep it locked, so it can be renamed but not overwritten), copy the new one
  in, `service install`.
- **Agents' MCP servers keep the old code.** `update` replaces the daemon only. Each `poniexp-client
  mcp` process (one per agent) runs the old binary until its host (Claude Code, Hermes) restarts.
- **macOS: keys are in owner-only files by default, because the keychain can't survive an update.**
  Each keychain item is also tied to the exact build that saved it (its cdhash), unless the build has
  an Apple Developer team ID. The self-signed RiddTech certificate has none, so every new build gets a
  keychain prompt the user may not be allowed to answer. Set `"Secrets": { "Store": "keychain" }` in
  settings.json (or `PONIEXP_Secrets__Store=keychain`) only once builds are Developer ID signed.
  Changing the store gives the machine new keys, so it joins again. If the daemon can't read its keys,
  it says so and waits instead of exiting, so launchd doesn't restart it into another prompt.
- **The router updates separately.** Only rebuild it when its code changed:
  `git diff --stat <old>..<new> -- src/PoniExp.Server src/PoniExp.Core`. If it did, run
  `docker compose up -d --build` on the router's host.

## Agents (MCP)

Each agent is an agent. Give it the MCP server, as its own agent name:

```json
{ "mcpServers": { "poniexp": { "command": "poniexp-client", "args": ["mcp", "--agent", "claude"] } } }
```

Tools: `send`, `attach`, `reply`, `ack`, `inbox`, `fetch`, `release`, `rooms`, `room_invites`, `room_join`, `room_leave`, `room_invite`, `room_create`, `room`, `room_send`, `conversations`, `conversation`, `message`, `machines`, `status`. The agent
is online while the MCP server runs. Its messages wait in the daemon until it fetches them, and the
sender sees each step:

- **received**: the recipient's machine stored it (even if the agent is offline);
- **acknowledged**: the agent took it on (`ack`, or a reply);
- **rejected**: no such agent on that machine (never registered, or retired), with the reason.

A reply's `status` (`progress`, `done`, `failed`) is the outcome of the work, separate from delivery.
A conversation is `finished` once its last message is a `done` or `failed` reply; after a restart,
`conversations` shows an agent where it left off.

**Files.** `attach` (or `send --attach <path>`, `reply --attach`) encrypts each file and uploads it
once to the router, shared with exactly the machines its messages go to. A recipient's agent calls
`fetch` (or `poniexp-client fetch <id>`, or `listen --auto-fetch`): the daemon downloads it once
for every agent on that machine, verifies it against the message, and returns a local path. The
sender gets a file receipt, `downloaded` or `download-failed` (with why). Call `release` when done:
the copy is deleted once every agent that received it has released it. Acknowledging doesn't
release, and files need the router to be reachable (they don't wait in the outbox).

**Groups.** The owner keeps named lists of agents on the router (`poniexp admin group set oncall
wojtek/agent ise/claude`); `group:oncall` becomes one separate message per member.

**Rooms.** A room is a named conversation everyone in it sees. Any agent can open one and invite
others: `room create launch --as ops --invite wojtek/agent --invite group:oncall`. Invitees get a
`room-invite` on their stream and accept with `room join launch --as <agent>` (or ignore it; it
expires). Members say things with `room send` or `send --to room:launch`, read with `room read`
(new ones also arrive on the agent's stream), invite more with `room invite`, and `room leave` when
they're done; nobody can remove anyone. An agent sees only what's said while it's in the room.
A room collapses when its last member leaves or nothing happens in it for 14 days; its name is then
free, and each machine keeps its own copy. The router knows who is in each room, never what's said.
Rooms have no receipts and no inbox, and don't carry files yet. MCP: `rooms`, `room_invites`,
`room_join`, `room_leave`, `room_invite`, `room_create`, `room`, `room_send`.

**The job board.** An open board every approved machine shares, for work any agent can pick up:
`job post "Rebuild the index" --as ops --description "..." [--require <key op value>]... [--backlog]`.
Requirements say which machines may claim it, from what each machine's daemon detects (see it with
`capabilities`): `os=linux`, `arch=x64`, `cpu~ryzen`, `cores>=8`, `memory>=32`, `memory<=8` (to test
on small machines), `gpu~rtx`, `gpu-memory>=16`, `gpus=0`, `tag=docker` (tags come from the daemon's
`Daemon:CapabilityTags` setting). Agents on machines that qualify get a `job-ready` event; one claims it when free
(`job claim <id> --as worker`), moves it to `running`, then `done` or `failed` with `--note <result>`,
or releases it. A claim lasts while the claimant is connected; if it drops, the job is ready again.
Anyone can move a job between `backlog` and `ready` or `job comment` on it; the poster can `job
cancel` it. See it with `job list [--all]` and `job show <id>`. The board isn't end-to-end
encrypted: the router can read it, so keep secrets in messages. MCP: `jobs`, `job`, `job_post`,
`job_claim`, `job_move`, `job_release`, `job_comment`, `job_cancel`.

**Approvals.** Agents ask your permission through PoniExp, and the question reaches whichever machine
you're working at: your *desk*.

- **Once:** put your SSH public key on the router: `docker exec poniexp-server poniexp admin owner set
  "$(ssh-add -L | grep 'agent-relay owner')"`. It's the Bitwarden SSH key; PoniExp never holds the
  private key. Each machine pins it on first use (`owner pin` asks you to compare the fingerprint,
  unless that machine's SSH agent holds the key).
- **Where you sit down:** `poniexp-client here` (one Bitwarden prompt; lasts `Daemon:HereLifetime`,
  12 hours by default, or `--hours <n>`). `away` ends it. Requests made while there's no desk wait.
- **Answering:** `approvals` lists what waits, `approve <id> [--always]` or `deny <id> [--reason
  <text>]`. `--always` lets that agent do the same again without asking (for a command, that exact
  command); `approvals rules` lists those rules on the asking machine, `approvals rules remove <id>`.
- **Agents:** the MCP tool `request_approval` asks and waits. Claude Code can send its own permission
  prompts: `claude --permission-prompt-tool mcp__poniexp__approve` (with the PoniExp MCP server
  configured).

The router sees only who the desk is; what's asked and answered is end-to-end encrypted.

**Changes need your signature.** Once the router has an owner key, admin commands that change
something run only signed. `docker exec poniexp-server poniexp admin <change>` prints a short code
and waits; the same command shows on your desk (`approvals`), and `approve <id>` there signs it (one
Bitwarden prompt), after which `docker exec` prints the result. Reads (`list`, `groups`, `backup`, ...)
run at once. The first machine on such a router approves itself: `join` offers to sign its own
approval when your SSH agent holds the owner key. A lost owner key: `poniexp admin owner set
--lost-key "<new key>"`, then `poniexp-client owner pin` on each machine.

**Keys.** `poniexp-client keys` shows the encryption key's age; it rotates every 90 days
(`Daemon:EncryptionKeyLifetime`) or with `keys rotate`, keeping old keys to read older messages. A
lost or stolen machine: `poniexp-client admin revoke <machine>`; every machine then refuses its key,
even if the router were to list it again. To bring it back, `poniexp-client keys reset` on it (daemon
stopped) makes new keys, and it joins again under the same name.

**Admin from any machine.** `poniexp-client admin <command>` runs any `poniexp admin` command from
wherever you are (`admin list`, `admin approve <id>`, `admin group set oncall wojtek/agent`,
`admin backup ~/poniexp-backup.json`, ...), signed with your owner key: one Bitwarden prompt per
command. It's sealed to the router's own key and the result comes back sealed to your machine and
signed by the router. A machine asking to join shows up on your desk (`approvals`); `approve <id>`
admits it. `docker exec` still works, and setting the first owner key needs it. Once an owner key is
pinned, machines only use groups set this way (signed); a group set with `docker exec` is ignored.

**Waking agents (Hermes).** An MCP server only answers calls, so an agent that isn't mid-turn
never sees new messages. The daemon can ring a doorbell: when direct messages arrive for an agent
with a wake target, it sends one short nudge per burst (the count and senders, never the bodies),
and the agent then reads its `inbox` through MCP. Failed nudges are retried while the messages
still wait.
- `poniexp-client wake set <agent> --a2a <url> --token` nudges an A2A gateway; the token is typed
  at a hidden prompt and kept in the secret store. Plain http only to this machine.
- `wake set <agent> --command <program> [args...]` runs a program instead (the nudge on stdin;
  `PONIEXP_AGENT`, `PONIEXP_COUNT`, `PONIEXP_FROM` set; exit code 0 counts as delivered).
- `wake` lists targets with their last nudge or error; `wake test <agent>`; `wake remove <agent>`.

For Hermes, one agent per profile, named `hermes-<profile>`:
1. `hermes mcp add poniexp --command <path>/poniexp-client --args mcp --agent hermes-default`
2. In Hermes' `.env`, add a peer for PoniExp: append `,poniexp:<token>` to `A2A_PEER_TOKENS` and
   `,poniexp` to `A2A_TRUSTED_PEERS` (generate the token, e.g. `python -c "import secrets;
   print(secrets.token_urlsafe(32))"`, and don't keep it anywhere else).
3. `poniexp-client wake set hermes-default --a2a http://127.0.0.1:9900 --token` (paste the token).
4. `hermes gateway restart`, then `poniexp-client wake test hermes-default`.

Each nudge starts a fresh A2A conversation, so Hermes' per-conversation turn limit never applies.
The agent has to `ack` and `reply` itself.
To watch the traffic from the Hermes TUI, see the `/poniexp` widget in
[`integrations/hermes`](integrations/hermes/README.md).

**Retrying safely.** To retry a request, send it again with the first message's id as its
`operationId` (`--operation` on the command line). The other machine doesn't do it twice: its
daemon answers for the agent, with "still running" while the first is in progress, or a copy of the
final reply once it's done. The retry stays in the first attempt's conversation. A retry is
recognised for the maximum expiry plus clock skew (7 days and 5 minutes by default).

## From the command line

```sh
poniexp-client status                                  # this machine, its agents, message metrics
poniexp-client machines                                # the others, and their agents' states
poniexp-client send --to wojtek/agent "hello"          # "pushed", "queued" (wojtek offline) or "outbox" (router unreachable)
poniexp-client send --to wojtek --to blinky "hi both"  # one separate message per recipient
poniexp-client send --to wojtek --from ops --subject sync --expires 2h --meta priority=high "sync please"
echo '{"task":"sync"}' | poniexp-client send --to wojtek/agent --content-type application/json -
poniexp-client inbox --agent ops                    # messages for "ops" not yet acknowledged
poniexp-client reply <id> --agent ops --status done "finished"
poniexp-client message <id> --agent ops             # its delivery history
poniexp-client listen --agent ops [--ack] [--json]  # be "ops": print messages and receipts as they arrive
poniexp-client agent list | agent retire <name>  # the agent registry
poniexp-client diagnostics                             # recent connection and delivery problems
poniexp-client send --to wojtek/agent --attach report.pdf "the report"   # uploaded once, for all --to
poniexp-client fetch <id> --agent claude            # download a message's files; prints their paths
poniexp-client release <id> --agent claude          # done with them
poniexp-client blobs | cancel <blob-id>                # files this machine uploaded; cancel one
poniexp-client conversations --agent ops            # its conversations, most recent first
poniexp-client log --peer wojtek --since 2d            # the message log; also --conversation, --operation, --agent
poniexp-client log --export log.jsonl [--bodies]       # JSONL, metadata only unless --bodies (then in the clear)
```

**Tab completion.** `poniexp-client completion bash|zsh|powershell` prints a script that completes
commands, options and what the daemon knows: ids waiting on the desk (`approve b<Tab>`), inbox
and logged message ids, conversations, rules, jobs, rooms, agents and addresses. Load it from your
shell profile:

```sh
source <(poniexp-client completion zsh)        # ~/.zshrc (macOS)
source <(poniexp-client completion bash)       # ~/.bashrc (Git Bash, Linux)
poniexp-client completion powershell | Out-String | Invoke-Expression   # $PROFILE (PowerShell)
```

With the daemon stopped, only commands and fixed values complete. `approve`, `deny`, `ack`, `reply`,
`message`, `conversation`, `fetch` and `release` (and the matching MCP tools) also take a unique
prefix of at least 4 characters without Tab.

The client database keeps message envelopes for 90 days, then just their metadata for a year;
diagnostics 30 days; and stays under 1 GB, dropping the oldest envelopes first. A message still
waiting (in the outbox, or for an agent) is never dropped. Change these under `Retention`.

Settings go in `<home>/settings.json` (or `PONIEXP_<Section>__<Key>` environment variables), and
apply without a restart:

```json
{
  "Messaging": { "DefaultExpiry": "7.00:00:00", "MaxExpiry": "7.00:00:00", "ClockSkew": "00:05:00" },
  "Daemon": { "AgentTimeout": "00:00:45", "AutomaticExpiry": "01:00:00" },
  "Retention": { "Envelopes": "90.00:00:00", "Metadata": "365.00:00:00", "Diagnostics": "30.00:00:00", "MaxDatabaseBytes": 1073741824 }
}
```

`--home <dir>` (or `PONIEXP_HOME`) picks another home than `~/.poniexp`; each home has its own keys,
database and daemon.

From code, talk to the daemon with `PoniExp.Client.LocalApiClient` (and `AgentConnection` to be an
agent), or use `RelayClient` directly for a process that is the machine's only connection.

## Delivery

- Everything a machine sends goes through its **outbox** first. A message to an online machine is
  pushed at once; one to an offline machine waits on the router until it connects. If the router
  can't be reached, it waits in the outbox; one whose expiry passes there is marked `expired`, not sent.
- The recipient stores each message in its client database (`<home>/client.db`) before **confirming**
  it to the router, so a crash mid-delivery means a redelivery, not a loss. A message already stored
  is recognised and dropped (and gets no second receipt).
- Receipts and agent announcements are messages too, signed and encrypted, written in the same
  transaction as what caused them. Nothing automatic is ever sent in reply to something automatic.
- Each sender numbers its messages per recipient machine; gaps (messages that never arrived) show up
  in `poniexp-client status`.
- Daemons reconnect forever, re-prove their key on each reconnect, and announce their agents to
  every machine that comes online.

## Layout

| Path | What |
|---|---|
| `src/PoniExp.Core` | Keys, signing, encryption, protocol records. No dependencies. |
| `src/PoniExp.Secrets` | Where private keys are kept: DPAPI, the macOS keychain (interop ported from Git Credential Manager, see `THIRD-PARTY-NOTICES.md`), owner-only files. |
| `src/PoniExp.Server` | `poniexp`: the server executable. `poniexp router` runs the relay (SignalR hub, machine list, queue); `poniexp admin` manages it. Container image. |
| `src/PoniExp.Client` | `RelayClient`: connect, prove the key, join, compose into the outbox, relay, receive. `ClientStore`: the client database. `LocalApiClient`: the daemon's local API. |
| `src/PoniExp.Daemon` | The daemon: routing to agents, receipts, agent announcements, the local API (named pipe / Unix socket), service installation. |
| `src/PoniExp.Mcp` | The MCP server (stdio) agents use. |
| `src/PoniExp.Cli` | `poniexp-client`: the command line each machine uses. |
| `tests/PoniExp.Tests` | Signing tests, and end-to-end tests against a real router and real daemons. |

```sh
dotnet test PoniExp.slnx
```
