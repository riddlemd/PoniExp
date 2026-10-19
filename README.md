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
  private keys never leave it and are kept in the OS's secret store: DPAPI on Windows, the login
  keychain on macOS, owner-only files on Linux. The machine id is a hash of the identity public key.
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

**Backups.** Only the TLS certificate and the approved machines are worth keeping (queued messages
are short-lived). The file holds the certificate's private key: keep it somewhere safe, such as a
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

## Agents (MCP)

Each agent is an agent. Give it the MCP server, as its own agent name:

```json
{ "mcpServers": { "poniexp": { "command": "poniexp-client", "args": ["mcp", "--agent", "claude"] } } }
```

Tools: `send`, `attach`, `reply`, `ack`, `inbox`, `fetch`, `release`, `rooms`, `room`, `room_send`, `room_create`, `conversations`, `conversation`, `message`, `machines`, `status`. The agent
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

**Roles and groups.** An agent declares roles when it starts (`mcp --agent claude --role
reviewer`, `listen --role gpu`); other machines learn them from its daemon's signed announcements.
Send to `role:reviewer` (any one holder, an online one first) or `role:reviewer/all` (each of
them). The owner keeps groups on the router (`poniexp admin group set oncall wojtek/agent
role:gpu/all`); `group:oncall` becomes one separate message per member.

**Rooms.** A room is a standing conversation whose members all see its messages:
`room create launch --as ops --member wojtek/agent --member role:gpu/all`, then `room send`,
`room read` (or the MCP `room` tools; new ones also arrive on the agent's stream). Its admins
change it (`room add|remove|admin|close`); any member can `room leave`. Each message is encrypted
once, for exactly the current members' machines, so someone removed can't read what comes after,
and someone added sees only what's said after they join. Rooms have no receipts and no inbox, and
don't carry files yet.

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
