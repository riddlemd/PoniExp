# PoniExp plan

The roadmap from a working relay to a coordination service for agents on several machines: what is
built, what each phase adds, what has been decided, and what is still open.

Every phase lands as its own commits. A phase is done when its tests pass three runs in a row and
it has been checked live against the `poniexp-server` container. Phases marked **workshop** start
with a design discussion before any code.

Phases only group the work into commits. Nobody uses PoniExp until every phase is done, so a phase
only has to build on the ones before it; a protection that arrives in a later phase isn't a gap.

## Built

- **Identity:** an identity key (ECDSA P-256) and an encryption key (ECDH P-256, signed by it) per
  machine, kept in the OS's secret store; the machine id is a hash of the identity public key.
- **Joining:** a signed join request (public key, name, description), compared by join code and
  approved with `docker exec poniexp-server poniexp admin approve`.
- **Connecting:** a signed challenge on every connect; only approved keys can send, receive or list
  machines.
- **Messages (phases 1-2):** format v2/v3, one recipient each, signed over every field and encrypted
  end to end; expiry, replay and sequence checks on the recipient by its own clock.
- **Delivery:** queued for offline machines (7 days), kept until the recipient confirms them.
- **Daemon (phase 3):** the machine's one connection; local API over a pipe or socket; agents with
  heartbeats and announcements; `received` / `acknowledged` / `rejected` receipts; the outbox; MCP
  server; `doctor`; `service install` in user and system modes; self-contained single-file builds.
- **Presence:** who's online, pushed to every machine.
- **TLS:** a self-signed certificate made once by the server and pinned by clients.
- **Server (phase 4):** SQLite storage (imported from the JSON files), connection and join limits by
  what a connection proved, join expiry, `clear-pending`, `/health` and the Docker health check,
  backup and restore.
- **History (phase 5):** conversations (open or finished), doing a request once (retries answered by
  the daemon), `log` with filters and JSONL export, retention with a size cap, the router's traffic
  log (`admin traffic-log`).
- **Files (phase 6):** blobs encrypted in 4 MB AES-GCM chunks with an encrypted manifest, uploaded once
  per send and shared with its recipients' machines; tickets; verified fetch shared by local agents;
  file receipts; release; limits with per-machine overrides; the message queue's own cap; `send/reply
  --attach`, `fetch`, `release`, `blobs`, `cancel`, `listen --auto-fetch`; MCP `attach`, `fetch`, `release`;
  `admin blobs`. Protocol 5.
- **Rooms and groups (phase 7):** owner groups
  (`group:x`, `admin group`), named rooms with a signed invite/join/leave log on the router,
  envelopes sealed for exactly the members' machines, collapse when empty or idle, statistics
  afterwards (`admin rooms`). Endpoints are called agents. Protocol 6.
- **Job board (phase 8):** an open forum on the router: signed job actions, columns from backlog to
  done / failed, leases renewed by the claimant's daemon, requirements matched against each machine's detected
  capabilities, `job-ready` events to machines that qualify, retention,
  backups and re-posting by posters' daemons. Protocol 7.
- **Executables:**
  - `poniexp` (`src/PoniExp.Server`), with the subcommands `router` and `admin`.
  - `poniexp-client` (`src/PoniExp.Cli`).
  - The `poniexp-server` container.

## Decisions so far

| Topic | Decision |
|---|---|
| Encryption | End to end ("option B"). **Everything that can be encrypted is**; the server sees only what routing needs. |
| Keys | Two per machine: the identity key (signing) and an encryption key (ECDH P-256), signed by the identity key. Rotating the encryption key doesn't change the machine's identity. |
| `operationId` | Not a nonce: a nonce must never repeat, while `operationId` deliberately repeats across retries of one action. Optional; defaults to the message id. |
| Addressing | A daemon per machine owns the only connection; agents are addressed as `machine/agent`. |
| Recipients | Every message has exactly one recipient (`machine/agent`). Sending to several means sending separate messages, each with its own id, sequence, envelope and log entry; no message names another recipient. A file is uploaded once and shared with all the recipients it is sent to (phase 6). |
| Not multi-tenant | A message, and the conversation it belongs to, is between one sender agent and one recipient agent. A send to several starts a separate conversation with each. Conversations with several members come with groups and rooms (phase 7). |
| Success path | A message is assumed to be going fine unless the recipient returns a failure. Replies carry `progress`, `done` or `failed`; there is no "accepted". |
| Separate tracks | Three things are tracked separately and never share a field: **delivery** of a message (receipts `received`, `acknowledged`, `rejected`), the **outcome** of the work it asked for (a reply's `status`), and **file transfer** (file receipts `downloaded`, `download-failed`). A reply's `status` is only ever about the work. |
| Message types | `type` is protocol level and hard-coded: a closed C# enum (`message`, `receipt`, `file-receipt`, `agent`, later `job`, `room`) that only PoniExp defines, because it decides how the daemon handles a message. Higher-level protocols between agents are not types: they ride on `type: message`, defined by its `contentType`, `metadata` and `body`, which agents are free to fill as they like. There is no reply type: a reply is a `message` with `replyTo` set. |
| Agent registry | Every agent that has ever registered with a daemon is remembered (`agents`). Registered but not connected is **offline**: its messages wait until it returns or they expire. Never registered, or retired with `poniexp-client agent retire <name>`, is **unknown**: the message gets a `rejected` receipt. |
| Automatic messages | `receipt`, `file-receipt` and `agent` are automatic. Nothing automatic is ever sent in response to something automatic; they expire after a short time (default 1 hour, a setting) and aren't retried after that; at most one goes out per message per event. They get rows in `messages` (so duplicates and sequence numbers work as for any message) but are never shown to agents. An expired message sends nothing back: the sender marks it `expired` by its own clock. |
| Hermes bridge | One agent per Hermes profile, named `hermes-<profile>` (e.g. `wojtek/hermes-default`), since each profile has its own memory, skills and gateway. Each has its own gateway URL (configuration) and its own A2A token, registered with that profile's Hermes and kept in PoniExp's secret store. |
| Daemon start-up | Two modes. **User mode** runs at logon as the user: the per-user `Run` registry key (Windows; a logon task would need admin), a LaunchAgent (macOS), a systemd user unit (Linux). **System mode** runs at boot: a Windows service under its own virtual account `NT SERVICE\PoniExp` (keys in that account's DPAPI, data in `%ProgramData%\PoniExp`, the local pipe restricted to the owning user; never a "do not store password" task), a macOS LaunchDaemon (keys in the System keychain), a systemd user unit with `loginctl enable-linger` (Linux). |
| Connection limits | By what a connection has proved, because behind port forwarding (Rancher Desktop) every client arrives from the same address. A connection that hasn't proved a key, or whose key is neither approved nor pending, must authenticate within 10 s, and at most 20 such connections exist at once. An approved or pending machine may hold at most 5 connections. At most 10 new join requests per hour across the router. Per-address limits (10 unauthenticated connections, 5 join requests an hour) apply on top, except to `SharedAddresses`: addresses known to stand for many clients, such as the port forwarder's. All of these are settings. |
| Backups | `poniexp admin backup` writes the TLS certificate (with its private key) and the approved machines to one owner-only file, or to stdout, for the owner to keep (e.g. as a Bitwarden attachment). `admin restore` puts them back. Queued messages, files and the traffic log aren't backed up. |
| Event integrity | Client events aren't hash-chained: a local process could rewrite a whole chain anyway (Local trust), and the signed envelopes already prove what was said. |
| Doing a request once | A retry is a message with the same `operationId` from the same sender agent. The recipient's daemon handles it, and the agent never sees a duplicate. While the first is still running (no `done` or `failed` reply yet), the daemon answers with a `progress` reply, "still running". Once it's finished, it re-sends a copy of the final reply. `operationId` defaults to the message id, so only deliberate retries are deduplicated. The record is kept for the maximum expiry plus skew. |
| Log export | `poniexp-client log --export` writes metadata only; `--bodies` decrypts bodies into it (they are then on disk in the clear). |
| Traffic log | Off by default (`poniexp admin traffic-log on`), kept 30 days. Besides what the server can see about each message (time, sender and recipient machine, id, size, sequence), it records the event (relayed, delivered, confirmed, expired, refused) and refusals with their reason (bad signature, limits, join requests), so delays and attacks show. |
| Roles | Removed. Agents carry no roles; addressing is by `machine/agent`, groups and rooms, and jobs match machines by requirements. |
| Capabilities and requirements | Each daemon detects what its machine has (OS, version, architecture, CPU, cores, memory, GPUs and their memory, plus tags from its settings) and reports it to the router on each connect. A job lists requirements (`key op value`: `=`, `!=`, `>=`, `<=`, `~`), all of which a machine must meet to claim it; minimums and maximums both, so `memory<=8` keeps a low-end test off a big machine. The router enforces them on the claim, against what the machine reported. |
| Permission level | No machine-to-machine rules: allow by default. Approvals (phase 9) are the permission system: an agent asks, you answer at your desk (`here`, signed with the owner key). Agents are labels, not identities. |
| Owner key | Your Bitwarden SSH key ("agent-relay owner", ssh-ed25519) through the SSH agent, pinned on every machine; it signs `here` (one prompt per desk). |
| Naming | What was called an endpoint is an **agent**: addresses are `machine/agent`, flags `--agent`, the automatic announcement type `agent`. Agent names are labels, not identities. |
| Job board | An open forum for every approved machine: jobs are in the clear on the router (signed changes, approved machines only) so a dashboard can show them. Any agent posts and comments; an agent on a machine meeting the job's requirements claims it under a lease its daemon renews, and moves it through backlog, ready, claimed, running, done / failed. Live jobs are in `admin backup`, and posters' daemons re-post their open jobs. GitHub issues stay outside PoniExp. |
| Web interface | Its own workshop phase (12), after owner admin, before the Hermes bridge. |
| Tray app | Built on Oriel (the user's Electron for .NET), not Avalonia; the last phase (14), waiting for Oriel to ship and finish `Notification`. |
| Rooms | Named (unique on the router while live), created by any agent; members invite, invitees accept, members leave; no admins, no removals. Membership is a log of signed events the router keeps and orders and can see (agents included); each message's key is wrapped for exactly the current members' machines, checked by the router. A room collapses when empty or idle (`RoomIdleExpiry`); the router then keeps only stats, per machine (no subjects, bodies or sending agents). |
| Groups | Owner-defined lists of addresses on the router (`admin group`), resolved by the sender's daemon into separate messages. |
| Local trust | Every local process running as the machine's user is trusted; it could read the user's keys anyway. The daemon scopes what each agent is shown, but that is convenience, not a security boundary. |
| Storage | SQLite, bundled in the app (`Microsoft.Data.Sqlite`). Nothing for the user to install. |
| Client database | One SQLite database per machine (see Client database below). Each table is created by the phase that first needs it. |
| Confirming | The client stores a message before confirming it to the server. A crash in between causes a redelivery, never a loss; the redelivered copy is recognised as a duplicate and dropped. |
| Expiry | `expiresOn` is never more than the maximum expiry out (default 7 days); an unset `expiresOn` means the default expiry (also 7 days). The relay clamps any later value to the maximum from when it received the message; the recipient clamps it to the maximum from `createdOn`. Both check expiry by their own clock, with a skew tolerance (default 5 minutes), so replay protection doesn't depend on the server. |
| Configuration | Every tunable value (expiry defaults and maximums, skew tolerance, retention, limits, timeouts) is a setting with a default, bound to an options class. Singletons and hosted services read it through `IOptionsMonitor<T>` (`CurrentValue` at the point of use, so changes apply without a restart); scoped services through `IOptionsSnapshot<T>`. Plain `IOptions<T>` isn't used for these. Options are validated at startup (`ValidateOnStart`). |
| Sequence gaps | Counted as a metric, not reported as warnings. A gap is normal when an expired message is dropped, and can't be told apart from a deliberate drop. |
| Join requests | Expire after a configurable time. |
| Relay data | Short-lived by design. Only the certificate and the approved machines are backed up (see Backups). |
| File limits | A quota per sender **and** a cap per recipient, plus limits per file and for the whole relay. No per-sender share of a recipient's cap. Files are something machines fetch deliberately; normal messages must always get through. |
| File sharing | A blob is shared only with the machines named when it is uploaded. Sending the file to anyone later means uploading it again as a new blob. |
| Reaching the server | Over the VPN for now. A router port forward is only considered after phase 4's hardening. |
| Naming | Timestamps end in `On` / `_on` (`createdOn`, `expires_on`); never `At` / `_at`. |
| Deferred | CI. Owner admin from anywhere, and key rotation and revocation, come after everything else. |

## Client database

One SQLite database per machine. Only the daemon (before phase 3, the CLI) writes to it; agents
read it through the daemon's local API. Each agent is shown only its own agent's messages
(scoping, not a security boundary; see Local trust). One database per machine, with agent
columns, not one per agent.

The phase that creates each table is in brackets. Columns are a draft; see Open questions.

`messages` (phase 1): one row per message, and so one per recipient. A send to three agents is
three messages and three rows. Written once, never edited (except dropping the envelope at the
end of its retention).
- Keyed by `(from_machine, id)`. This row is also the replay protection: a message whose key is
  already here is a duplicate. There is no separate `processed` table.
- `from_machine`, `from_agent`, `to_machine`, `to_agent`
- `conversation_id`, `reply_to`, `operation_id`, `type`, `status`, `content_type`, `subject`,
  `sequence`
- `created_on` (sender, signed), `relayed_on` (server), `stored_on` (local), `expires_on` (after
  clamping)
- `envelope`: the original signed message; from phase 2, encrypted. The body is **not** stored
  decrypted; the daemon decrypts the envelope when an agent reads it (the content key is wrapped
  for the sender too, so outbound envelopes are readable). The envelope is already protected at
  rest, and its signature can be re-checked later.
- The searchable metadata columns are stored in the clear locally; bodies are not, so there's no
  full-text search inside them.
- There's no `direction` column: direction is derived by comparing `from_machine` against the
  machine's id. Indexes on `from_machine` and `to_machine`.

`sequences` (phase 1): per peer machine, the next outgoing `sequence` and the last one seen
incoming.

`metrics` (phase 1): counters per peer machine, e.g. sequence gaps and duplicates, with the time
each was last seen.

`message_events` (phase 3): append-only. Delivery only; the outcome of the work lives in replies,
and files have their own events.
- `from_machine`, `message_id`, `direction` (in or out: a message a machine sends itself has both),
  `agent`, `event`, `occurred_on`, `detail`
- Inbound: `stored` (`received` sent) → `fetched` → `acknowledged` (sent), or `rejected` (sent,
  with a reason) / `expired` (nothing sent; see Automatic messages).
- Outbound: `queued` → `relayed` → `received` → `acknowledged`, or `rejected` (by the recipient,
  with its reason) / `failed` (the server refused it) / `expired`. `relayed` is the server's word;
  `received`, `acknowledged` and `rejected` are the recipient's signed receipts.

`message_state` (phase 3): the furthest event per message and direction, updated in the same
transaction as the event. A late event never moves it back (a `received` after `acknowledged`), and a
final one (`rejected`, `failed`, `expired`) is never left.

`bridge_tasks` (phase 13): for messages delivered through the Hermes bridge, `message_id`,
`a2a_task_id`, `context_id`, the peer (`machine/agent`) the task belongs to, and the task's last
known state.

`agents` (phase 3): every agent that has registered with this daemon: `name`, `registered_on`,
`last_seen_on`, `retired_on`. The agent registry (see Agent registry).

`peer_agents` (phase 3): what other machines have announced: `machine`, `agent`, `state`
(online, left, timed out), `counter` (the announcing daemon's), `changed_on`. Each announcement is
the machine's complete list, so a newer one replaces all of that machine's rows; `peer_counters`
keeps the highest counter seen per machine, and a lower or equal one is ignored.

`daemon_state` (phase 3): this daemon's own counters (the announcement counter), which only increase.

`diagnostics` (phase 3)
- `occurred_on`, `level`, `category` (connection, signature, version, delivery), `detail`, and the
  related message or machine.

`conversations` (phase 5)
- `id`, `subject`, `local_agent`, `peer` (the one `machine/agent` on the other side),
  `started_by`, `started_on`, `last_activity_on`, `state`

`operations` (phase 5): doing a request once.
- Keyed by `(from_machine, from_agent, operation_id)`.
- `state` (running or done), `result_message_id`, `keep_until`.
- A retry while running gets "still running"; a retry after it's done gets the stored result.

`blobs` (phase 6): one row per blob on this machine, however many local messages refer to it.
- `blob_id`, `size`, `local_path`, `fetched_on`, `released_on` (released on the server),
  `deleted_on` (local copy removed)

`attachments` (phase 6): which message refers to which blob.
- `message_id`, `blob_id`, `name`

`attachment_events` (phase 6): append-only, the file track, separate from message delivery.
- `message_id`, `blob_id`, `agent`, `event`, `occurred_on`, `detail`
- Inbound: `fetched` → `verified` (`downloaded` sent) → `released` (the agent is done with it), or
  `download-failed` (sent, with a reason).
- Outbound: `uploaded` → `downloaded`, or `download-failed` (with the recipient's reason) /
  `cancelled` / `expired`. `downloaded` and `download-failed` are the recipient's signed file
  receipts.

---

## Phase 1: Message format v2 (done)

The message format changes here and again in phase 2. Nothing has joined yet, so both changes are
free.

**Addressing:** `to` and `from` are `machine/agent`. The server routes to the machine; the
recipient's daemon routes to the agent. A message has exactly one recipient; sending to several
sends one message to each (see Recipients).

**Visible to the server** (needed for routing and expiry):

| Field | Purpose |
|---|---|
| `id` | Unique per message (the nonce). |
| `fromMachine`, `toMachine` | Routing, and whose signature to check. |
| `expiresOn` | The server stops delivering after this. At most the maximum expiry out; later values are clamped. |
| `sequence` | Per sender → recipient machine; gaps show messages that never arrived. |
| `size` | Length of the encrypted payload. |

**Encrypted in phase 2:**

| Field | Purpose |
|---|---|
| `fromAgent`, `toAgent` | The agent on each machine. |
| `conversationId` | Groups an exchange into a thread; replies inherit it. |
| `replyTo` | The message id this answers. |
| `operationId` | The same across retries of one action, so the recipient does it once. |
| `status` | For replies: `progress`, `done` or `failed`. Anything but `failed` is the success path. |
| `subject` | A readable title for conversation lists. |
| `type`, `contentType` | What the message is, from a closed enum (see Message types), which decides how the daemon handles it; and how to read the body. |
| `metadata` | A small key/value map, free for agents to use (e.g. for their own higher-level protocols). |
| `body`, `createdOn` | The content, and the sender's timestamp. |

In phase 1 the second group is signed but not yet encrypted. The split is fixed now so the format
changes only once more.

**Client database:** created in this phase with `messages`, `sequences` and `metrics` (see Client
database). A message is stored before it is confirmed to the server.

**Duplicates:** a message whose `(from_machine, id)` is already stored, whether redelivered after a
crash or replayed, is confirmed to the server again and dropped quietly. It isn't handled twice,
gets no second receipt and isn't logged as `rejected`; it is counted in `metrics`.

**Expiry:**
- The sender's client uses the default expiry when `expiresOn` is unset, and never sets it more
  than its maximum expiry ahead.
- The relay clamps any `expiresOn` beyond its maximum expiry (counted from when it received the
  message) to that maximum, and stops delivering at that time.
- The recipient clamps `expiresOn` to at most `createdOn` + its maximum expiry, and rejects (as
  `expired`) a message whose clamped expiry has passed by its own clock. It also rejects a
  `createdOn` further in the future than the skew tolerance.
- Both ends allow a skew tolerance. So a message is only ever accepted within a
  bounded window, and replay protection only needs to remember ids for that long, whatever the
  server does.

**Settings** (see Configuration):

| Setting | Where | Default |
|---|---|---|
| Default expiry (unset `expiresOn`) | client | 7 days |
| Maximum expiry (clamp) | relay and client, each its own | 7 days |
| Clock skew tolerance | relay and client, each its own | 5 minutes |

The relay's clamp is the one that limits delivery; the recipient's clamp, from its own setting,
is what bounds its replay window, so it doesn't take the relay's value on trust.

**Sequence gaps** are counted in `metrics`, not reported as warnings.

**Also in this phase:**
- A protocol version check at connect. Incompatible clients get a clear "upgrade" refusal.
- Rename existing `*At` members (`CreatedAt`, `RequestedAt`, `DecidedAt`, `QueuedAt`, `LastSeen`,
  `PresenceChange.At`) to `*On`. Rename the server-side "acknowledge" to "confirm".

**Done when:**
- `SendAsync` and `ReplyAsync` take the new fields, and the CLI can set them (`--reply-to`,
  `--expires 10m`, `--subject`, ...). Several `--to` send one message per recipient.
- Tests show:
  - the signature covers every field
  - an expired message isn't delivered
  - an `expiresOn` beyond the maximum is clamped by the relay and by the recipient, and an unset
    one gets the default
  - changing the expiry or skew settings while running takes effect without a restart, and an
    invalid value (e.g. a negative skew) stops startup
  - an expired message delivered anyway (by a misbehaving server) is rejected by the recipient,
    as is one with a `createdOn` too far in the future
  - a gap in sequence numbers is counted, including after a client restart
  - a duplicate, whether a redelivery after a crash between storing and confirming or a replay,
    is confirmed, dropped and handled only once, including after a client restart
  - sending to several recipients produces separate messages, none of which names another
    recipient
  - a mismatched protocol version is refused

## Phase 2: Encryption (done)

- **An encryption key per machine:** created next to the identity key, signed by it, and sent in the
  join request (required). The server distributes it; clients check the signature before encrypting
  to it, so the server can't substitute its own.
- **Bodies and the encrypted fields** from phase 1 are encrypted to the recipient:
  - A random content key per message, AES-256-GCM.
  - The content key is wrapped twice: for the recipient, and for the sender's own machine so it
    can read what it sent. Each wrap uses **ephemeral-static** ECDH P-256: a fresh key pair per
    wrap, combined with the target machine's encryption key, then HKDF-SHA256. No two messages
    ever derive the same wrapping key.
  - The server-visible header (`id`, `fromMachine`, `toMachine`, `sequence`, `expiresOn`) is bound
    into the ciphertext as AES-GCM associated data, and `id`, `fromMachine` and the target
    machine's id go into the HKDF `info`. Someone who strips the signature and re-signs the
    ciphertext as themselves gets a message that fails to decrypt, so content can't be
    re-attributed to another sender.
  - The sender's signature covers the encrypted bytes.
- **Keys are stored securely:**
  - Windows: DPAPI, user scope (so the daemon runs as the user; see phase 3).
  - macOS: the Keychain (KHost's interop, ported from Git Credential Manager).
  - Linux: owner-only files.

  This follows KHost's secret-store approach (`PoniExp.Secrets`), except that KHost uses Credential
  Manager on Windows and libsecret on Linux; here DPAPI files and owner-only files, as decided above.
- **Done when:**
  - a server-substituted key is rejected by the client
  - messages on the server's disk are unreadable
  - a tampered message fails to decrypt
  - a ciphertext re-signed by a different machine fails to decrypt
  - two messages to the same recipient use different wrapping keys
  - the sender can decrypt its own stored outbound envelope

## Phase 3: Daemon and MCP (done)

Agents on different machines talk through their daemons, with MCP as the agents' interface. Adds
`message_events`, `message_state`, `agents`, `peer_agents` and `diagnostics` to the client
database. Pushing messages into Hermes (the Hermes bridge) is phase 13.

- **`poniexp-client daemon`:** the machine's only connection to the server. It handles local
  routing to agents, the outbox, and the client database.
  - **Outbox:** a message sent while the router is unreachable waits in the outbox. One whose expiry
    passes before it is relayed is marked `expired` instead: its `createdOn` is signed and the
    recipient's clamp counts from it, so relaying it late would only get it refused.
- **Local API** on a Unix socket or named pipe, used by the CLI, the MCP servers and (phase 13) the
  Hermes bridge.
- **`poniexp-client mcp`** (stdio). Tools: `send`, `reply`, `ack`, `inbox`, `conversation`,
  `message` (one message and its delivery history), `machines`, `status`; later `attach` and `fetch`.
- **Agent announcements:** an agent (an MCP server, the Hermes bridge, a CLI `listen`)
  registers with its daemon by name and keeps the registration alive with a heartbeat.
  - The daemon announces each change to every approved machine: an agent came **online**,
    **left** (unregistered cleanly), or **timed out** (missed its heartbeat).
  - Announcements are messages of type `agent`, encrypted and signed like any other, one per
    machine (agent names are encrypted, so the server can't carry them in its presence list).
    They are automatic (see Automatic messages).
  - Each carries the announcing daemon's counter, which only ever increases. A receiver keeps only
    the newest per machine (`peer_agents`), so a replayed or late "left" can't override a newer
    "online".
  - When the server reports another machine coming online, the daemon sends it its full current
    agent list, so a machine that was away catches up.
  - When a machine goes offline, all its agents count as offline.
  - The MCP `machines` tool shows each machine's agents and their state.
  - A message for an agent that is offline is held by the recipient's daemon until the agent
    returns or the message expires. A message for an unknown agent (never registered, or retired
    with `poniexp-client agent retire <name>`) gets a `rejected` receipt ("unknown agent").
- **Receipts:** delivery only, each sent by the recipient as a signed, encrypted message of type
  `receipt`, so the server can't fake any of them. The outcome of the work comes back separately,
  as a reply with a `status`.
  - **`received`:** the recipient's machine has picked the message up. The daemon sends it once the
    message has passed its checks (signature, expiry, duplicate) and is stored, whether or not the
    agent is online.
  - **`acknowledged`:** an agent has actually taken the message on. Fetching a message isn't
    enough; the agent acknowledges it explicitly (the MCP `ack` tool, or the local API). Replying
    acknowledges too. For the Hermes bridge, see phase 13.
  - **`rejected`:** the recipient's machine won't deliver it, with a reason (e.g. "unknown
    agent"). Sent instead of `received`.
- **`poniexp-client doctor`:** checks reachability, the certificate pin, clock skew against the
  server's tolerance, membership and the daemon.
- **`status`** (CLI and MCP) also shows the `metrics` counters.
- **Self-contained builds** for Windows, macOS and Linux, with no .NET install needed. The bundled
  SQLite library must unpack correctly from the single-file build; test it on each OS.
- **Running as a service:** `poniexp-client service install [--system]` sets the daemon up to start
  automatically, in user mode (at logon) or system mode (at boot); see Daemon start-up. On Windows,
  user mode is the per-user `Run` registry key with `conhost --headless` (no console window), not a
  logon scheduled task: creating a logon task needs admin. It isn't restarted if it stops.
- **Joining goes through the daemon** when one runs (`poniexp-client join`): in system mode only the
  daemon holds the keys.
  `service uninstall` and `service status` go with it. In system mode the daemon owns the keys
  (they are created under its account), and the CLI only ever talks to it over the local API.
- **`diagnostics`:** connection, signature, version and delivery problems are recorded, and shown by
  `doctor`.
- **Not verified live:** system mode (`service install --system`) on any OS: on Windows it needs an
  elevated prompt; and nothing on macOS (no Mac to hand). Their plans are covered by tests only.
- **Done when:**
  - An agent on one machine, through MCP, holds a conversation with an agent on another.
  - Tests show an agent's online, left and timed-out states reaching another machine, a machine
    that was offline catching up on reconnect, and a message to an unknown agent getting a
    `rejected` receipt, before and after it is retired.
  - Tests show a replayed or late announcement with a lower counter being ignored, a message whose
    expiry passes in the outbox being marked `expired` and never relayed, and nothing automatic sent
    in reply to something automatic.
  - Tests show `received` arriving while the recipient agent is offline, `acknowledged`
    arriving only after the agent acknowledges or replies (not when it merely fetches), and no
    second `received` for a duplicate.

## Phase 4: Server foundations (done)

- **SQLite** (`data/router.db`) replaces `machines.json` and `queue.json`, which are imported on first
  start and kept as `*.imported`. Files and jobs are added to it later.
- **Hardening before the port faces the internet** (required before any port forward), as in
  Connection limits:
  - a deadline to authenticate, a cap on unauthenticated connections, a cap per machine;
  - a router-wide join-request rate, with per-address limits except for `SharedAddresses`;
  - join requests that expire (`JoinRequestExpiry`, 24 h), and `poniexp admin clear-pending`;
  - a Kestrel connection cap as a backstop.
- **`/health`** and a Docker health check (`poniexp health`, against the loopback admin listener).
- **Backups:** `poniexp admin backup [path|-]` and `admin restore <path|->` (see Backups). Restoring
  a different certificate takes effect after a restart.
- **Done when:**
  - The router's state survives a restart from SQLite, and an existing `machines.json`/`queue.json`
    is imported once.
  - Tests show a connection that doesn't authenticate being dropped after the deadline; the
    unauthenticated cap; the per-machine cap; the join rate (router-wide and per address); a shared
    address exempt from per-address limits; a join request expiring (and its waiting client told);
    `clear-pending`.
  - A backup restored into an empty router gives the same certificate SHA-256 and approved machines.
  - Live: the container reports healthy; existing data is migrated; a backup taken with `docker exec`
    restores.

## Phase 5: History and logs (done)

### Client log

Completes the client database as a log. Adds `conversations` and `operations`.

What it's for:
- history
- conversation memory for agents after a restart
- doing a request once
- inbox state for each agent
- outbox and receipts
- diagnostics

**Retention, set per kind of data:**

| What | Default |
|---|---|
| `operations` | The maximum expiry plus skew: no retry can arrive after that. |
| Envelopes | 90 days, then the envelope is dropped and the metadata kept. |
| Metadata and events | 1 year. Must always be longer than a message can be accepted (maximum expiry plus skew), so duplicate detection holds; startup validation refuses settings that break this. |
| Diagnostics | 30 days. |
| Size cap | 1 GB; the oldest envelopes are dropped first. |

A conversation's `state` is derived: `open`, or `finished` once its last message is a reply with
status `done` or `failed` (a later message reopens it).

**CLI:** `poniexp-client log` by conversation, operation, peer or agent, with a JSONL export
(`--export`, metadata only unless `--bodies`).

**Done when:**
- A restarted agent gets its conversation back (`conversation`, `log`).
- Tests show a retry while the first is running getting "still running", a retry after it's done
  getting the final reply again, the agent seeing neither, and a retry after the record expired being
  treated as new.
- Tests show each retention rule (envelopes, metadata, diagnostics, operations, the size cap) and
  settings that would break duplicate detection refusing to start.
- Tests show the traffic log recording each event and refusal only while it's on, and pruning.

### Server traffic log

- Off by default; `poniexp admin traffic-log on|off`, with its own retention.
- Records only what the server can see: `occurred_on`, sender machine, recipient machine, message
  id, size, sequence. Agents are encrypted, so the log shows which machines talk, not which
  agents.

## Phase 6: Encrypted files (done)

A file is uploaded **once**, however many recipients it goes to. Each recipient gets its own
message (see Recipients) pointing at the same blob. Adds `blobs` and `attachments` to the client
database.

- **Encryption:**
  - A random file key per upload.
  - 4 MB chunks, each encrypted with AES-256-GCM. The chunk index and a last-chunk flag are bound
    into each chunk, so the server can't reorder, drop or truncate them unnoticed.
  - An encrypted manifest holding the filename, content type, real size and chunk count.
  - The blob id, the file key and the hash of the chunk-hash list go inside each recipient's
    message. The message is already encrypted to that recipient (and to the sender), so the file
    key needs no wrapping of its own, and the hash is covered by the message's signature.
- **Transfer:**
  1. `BeginUpload` names the machines the blob is shared with, and returns a blob id and a
     short-lived ticket covering one blob and one direction. The list can't be extended later.
  2. Chunks go up with `PUT /blobs/{id}/chunks/{n}`; `GET /blobs/{id}/status` shows what's
     there, so uploads can resume.
  3. `CompleteUpload` seals the blob. The sender then sends one message per recipient.
  4. A recipient gets a download ticket from `RequestDownload` (only granted to machines the blob
     is shared with), then fetches chunks with `GET /blobs/{id}/chunks/{n}`.
- **Storage:**
  - Each upload is its own blob, stored under a random id, shared only with the machines named in
    `BeginUpload`. Sending the file to anyone else later means uploading it again as a new blob.
    Nothing is shared between separate uploads, even of the same file, because that leaks
    information and lets someone fetch a file they never had.
  - A blob is readable only by its sender and the machines it is shared with.
  - Recipients aren't told who else a blob is shared with: status, tickets and refusals they see
    never list the other machines. (The server knows, as it routes every message anyway.)
- **Several agents on one machine:** if more than one local agent receives a message for the
  same blob, the daemon fetches it once, verifies it once and keeps one local copy.
- **File receipts:** separate from message receipts and from a reply's `status`. Sent by the
  recipient as signed, encrypted messages of type `file-receipt`, one per message that carried the
  file:
  - **`downloaded`:** the blob was fetched and verified on the recipient's machine.
  - **`download-failed`:** with a reason (chunk or hash mismatch, blob expired, cancelled by the
    sender, over a limit).
- **Local copy:** an agent is done with a file when it releases it (the MCP `release` tool or the
  local API). The local copy is deleted when every local agent that received it has released
  it, or when its retention passes (as for envelopes: 90 days, or earlier under the size cap).
  Acknowledging the message doesn't release the file: that's when the agent starts using it.
- **Deletion:**
  - Each recipient machine releases the blob after a verified download.
  - The blob is deleted when every machine it is shared with has released it, after 7 days, or
    when the sender cancels it.
  - Unsealed uploads are dropped after 24 hours.
- **Limits** (configurable, with overrides per machine):

  | Limit | Default |
  |---|---|
  | Per file | 2 GB |
  | Per sender | 10 GB, counting a blob shared with several machines once |
  | Per recipient | 5 GB, counting a shared blob in full against each machine it is shared with |
  | Whole relay | 50 GB, plus a 5 GB free-disk floor |

  - Space is reserved when an upload starts.
  - Refusals say why ("ise has 4.8 of 5 GB waiting, offline since ...").
- **Message queue:** its own cap (about 100 MB queued per recipient), separate from files, so normal
  messages always get through.
- **CLI:**
  - `send --attach`, `fetch`, `release`, `listen --auto-fetch`
  - `blobs` and `cancel` (sender)
  - `poniexp admin blobs [clear --for <machine>]`
- **MCP:** `attach`, `fetch` and `release`.
- **Done when:** tests show a `downloaded` receipt after a verified fetch, a `download-failed`
  receipt for a tampered chunk and for a cancelled blob, one fetch for several local agents,
  and the local copy surviving acknowledgement and being deleted only after every local agent
  has released it.

## Phase 7: Rooms and groups (workshop, done)

Decided in the workshop (see the decisions table): groups are the owner's, rooms have no shared key.
Roles were built here and later removed (jobs match machines by requirements, phase 8).

### Groups

- A named list of `machine/agent` addresses, set by the owner:
  `poniexp admin group set <name> <address>...`, `group remove`, `groups`. Kept in `router.db`.
- `group:<name>` is resolved by the sender's daemon into separate messages, one per member. A group
  isn't a room: its members don't see each other.
- The router serves the lists, so a compromised router could point a group at another approved
  machine (it still can't read what it relays). Owner-signed lists can come with owner admin (phase 10).

### Rooms

Revised after review (the first build used admins and versioned snapshots; that is replaced).

- **A room** is a standing, many-sided conversation between agents, with a **name unique on the
  router while the room is live** and addressable as `room:<name>`. **Any agent can create one.**
  There are no admins, and no one can remove anyone: **members invite, invitees accept, members
  leave.** (Start a new room to be rid of someone.)
- **Membership is a log of signed events**, kept and ordered by the router:
  - `create` (by the creator, who is the first member), `invite`, `join` (by the invitee, accepting) and
    `leave`.
  - Each event is signed by its author's machine key and names the author agent, so the router can't
    forge or alter one, only withhold it. It carries that key (whose fingerprint is the machine id), so a
    log checks out without asking anyone, even after a machine is removed.
  - The room id is derived from the `create` event, so no other log can claim it.
  - An invite counts only from a member at that point in the log; a join only from an invited agent;
    a leave only from a member.
  - Every member machine (and an invitee's) gets the log, checks it from `create` on, and derives the
    members from it. A member is whoever has joined (or created the room) and hasn't left.
  - Invites are pending until accepted, and an agent declines by ignoring the invite (it expires,
    after `Daemon:RoomInviteLifetime`, default 7 days, at most 30).
  - The owner removing a machine adds a router `removed` event (unsigned), which members accept only for
    a machine no longer approved: its agents are out.
  - The router sends a room's log to every machine involved whenever it changes, and to a machine that
    connects before its queued messages; a daemon keeps a log only if it extends the one it has.
- **The router sees the membership:** which agents are in which room, and when they joined and left.
  That is a deliberate exception to "the router sees machines, not agents", needed for unique names
  and for collapsing empty rooms.
- **Messages:** one envelope per room message, addressed to the room, its content key wrapped for
  exactly the current members' machines. The router fans it out, and **refuses an envelope not wrapped
  for exactly those machines**, so a sender that missed a join or leave refreshes and seals again (no
  stale sender can reach someone who left). Contents, subjects, bodies and the sending agent stay
  end-to-end encrypted: the router checks the sending machine has a member in the room, and each
  member's daemon checks the sending agent was a member when it wrote the message.
- **History:** an agent sees messages from when it joined. Each machine keeps its own copy under the
  normal retention (phase 5). No receipts and no inbox in rooms: they're read like a channel.
- **Collapse:** a room collapses when its last member leaves, or when no member has said anything for
  `RoomIdleExpiry` (a setting, default 14 days). Its name is then free. Members' daemons are told,
  and keep their logs.
- **Stats:** on collapse the router compacts the room to statistics and drops the event log:
  - name and id, opened and collapsed (and why);
  - members, with when each joined and left;
  - per machine, message count and bytes (the sending agent is encrypted, so not per member);
  - each message's timestamp and size.

  No subjects or bodies: the router never has them. `poniexp admin rooms [--collapsed]` shows them.
- **CLI / MCP:** `room create <name>`, `room invite <room> <address>...` (groups resolved
  when invited), `room join <room>` (accept), `room leave <room>`, `room list`, `room invites`,
  `room read`, `room send`. MCP `rooms`, `room_invites`, `room_join`, `room_leave`, `room_invite`,
  `room` (read), `room_send`, `room_create`.
- **Not in this phase:** files in room messages (a shared copy needs its own release rule).
- **Settings:** `PoniExp:RoomIdleExpiry` (14 days) and `PoniExp:RoomStatsRetention` (365 days) on the
  router; `Daemon:RoomInviteLifetime` (7 days) on each machine.

**Done when:** tests show:
- a unique name refused while live and free after collapse;
- an invitee seeing nothing until it joins, then only from then on;
- an invite from a non-member refused (by the router and by members' daemons);
- a forged or altered event refused;
- a room message read by every member and no one else;
- a sender that missed a leave refused and re-sealing;
- collapse on the last leave and on idleness, with only the stats kept on the router.

## Phase 8: Job board (workshop, done)

An open forum for every approved machine: any agent posts work from whatever machine you're on, and
an agent that matches picks it up when it has capacity and moves it through the columns.

- **Open, not end-to-end encrypted.** The board is shared by every approved machine, so the router
  holds jobs in the clear (over TLS, reachable only by approved machines) and can show them on a
  dashboard (phase 12). Every change is signed by the machine that makes it and names its agent; the
  router checks the signature and the rules. Secrets don't belong on the board: send them as messages.
- **A job:** id, title, description (markdown), requirements, poster, column, claimant, lease, a
  result note, and a history of moves and comments, each with when and by whom.
- **Columns:** `backlog` → `ready` → `claimed` → `running` → `done` / `failed`.
  - Any agent moves a job between `backlog` and `ready`, and comments on any job.
  - **Claim:** a `ready` job, by one agent at a time, on a machine that meets all the job's
    requirements (checked by the router against what that machine reported).
  - **Lease:** the claimant's daemon renews it while the agent is online (`JobLease`, default 2
    minutes); when it runs out, the job goes back to `ready` and the history says so.
  - The claimant moves it to `running`, then `done` or `failed` with a result note, or releases it
    back to `ready`.
  - The poster can cancel an unfinished job: `failed`, "cancelled by the poster".
- **Finding work:** when a job becomes `ready`, online agents on machines that meet its requirements
  get a `job-ready` event on their stream. They claim when free, or browse the board (`job list` shows
  what this machine lacks for each job).
- **Results:** the result note is on the job, for everyone. Files and anything private go to the
  poster as an ordinary encrypted reply (`replyTo` the job's id is not used: jobs aren't messages).
- **Retention:** finished jobs stay on the router for `JobRetention` (90 days), then are dropped.
- **Backups:** live jobs are in `admin backup` (and restored with it). Each poster's daemon keeps its
  open jobs, and re-posts them under the same id if the router no longer has them (a lost volume).
- **GitHub issues:** not in PoniExp; an agent can mirror issues onto the board.
- **CLI / MCP:** `job post|list|show|claim|move|release|comment|cancel`; MCP `jobs`, `job`,
  `job_post`, `job_claim`, `job_move`, `job_release`, `job_comment`, `job_cancel`; `admin jobs [--all]` on the
  router. Approved machines read the board over the hub (`GetJobs`); phase 12 adds the web view.
- **Capabilities:** each daemon detects its machine (OS, version, architecture, CPU model, logical
  cores, memory as the runtime sees it, so a container's limit counts; GPUs and their memory from
  `nvidia-smi`, else the OS's display adapters) and adds tags from `Daemon:CapabilityTags`. It reports
  them to the router on each connect; `machines`, `capabilities` and `admin list` show them.
- **Requirements:** `key op value`, all must hold. Keys: os, os-version, arch, cpu, cores, memory,
  gpu, gpu-memory, gpus, tag. Ops: `=`, `!=`, `>=`, `<=` (numbers), `~` (contains). `memory>=2` with
  `memory<=8` targets small machines. The router checks a claim against what the claiming machine
  reported (its own word); the daemon checks first for a clearer message.
- **The owner removes a job** with `admin job remove <id>` (spam, or a poster that's gone).

**Done when:** tests show:
- a job posted on one machine is seen and claimed on another;
- a second claim is refused, and a claim from a machine not meeting the requirements is refused,
  by the router too;
- requirements match minimums, maximums, text and cards;
- a lease that isn't renewed returns the job to `ready`;
- only the claimant moves a claimed job on, and only the poster cancels;
- a forged or altered change is refused;
- `job-ready` reaches matching online agents only;
- finished jobs are dropped after `JobRetention`;
- a backup restores the live jobs, and a poster's daemon re-posts its open jobs to an empty router.

## Phase 9: Approvals and the owner (workshop, done)

The point of this phase: an agent that needs a human's yes reaches you wherever you are. Allow by
default: there are no machine-to-machine rules to configure; approvals are the permission system.

- **The owner key** is your SSH key in the Bitwarden SSH agent ("agent-relay owner", `ssh-ed25519`),
  used through the SSH agent on whatever machine you're at (Windows: the OpenSSH agent pipe; macOS and
  Linux: `SSH_AUTH_SOCK`). PoniExp never holds it.
  - The router keeps the owner's public key (`poniexp admin owner set "<ssh public key>"`).
  - Each machine pins it: `poniexp-client owner pin` shows its SHA-256 fingerprint (as `ssh-add -l`
    does) and pins it on a yes; it pins without asking when the local SSH agent holds that very key.
  - Owner signatures are SSH signatures (the `ssh-keygen -Y sign` format) under the namespace
    `poniexp`, so they can't be mistaken for an SSH login and can be checked with `ssh-keygen -Y
    verify`. Ed25519 is verified with BouncyCastle (.NET has no Ed25519).
- **`poniexp-client here`** makes this machine your approval desk: one Bitwarden prompt signs a claim
  (this machine, from now until `HereLifetime`, default 12 hours). The router checks it against the
  owner key, keeps the newest, and sends it to every machine; each daemon checks it against its pin.
  `poniexp-client away` ends it (signed by the owner, or sent by the desk machine itself). With no
  desk, requests wait.
- **Requests:** an agent asks through the MCP tool `request_approval` (a summary and details), or
  Claude Code sends its own permission prompts through `approve` (run it with
  `--permission-prompt-tool mcp__poniexp__approve`).
  - The agent's daemon first checks its "always allow" rules (below); a match is allowed at once.
  - Otherwise it sends an `approval-request`, end-to-end encrypted like any message, to the desk
    machine's daemon, and the agent's call waits until you answer or the agent gives up (a withdrawn
    request is marked `withdrawn` on the desk).
  - When the desk moves, pending requests are sent to the new desk.
- **Answering:** on the desk, `poniexp-client approvals` lists what waits (who asks, from which
  machine, what), `approve <id> [--always]` and `deny <id> [--reason <text>]` answer it (an
  `approval-answer`, signed by the desk machine). The requesting daemon accepts an answer only from
  the machine named by the current owner-signed `here`. A desktop notification comes with the tray app
  (phase 14).
- **"Always allow"** makes a rule on the requesting machine: that agent may use that tool without
  asking (for a shell tool, that exact command). `poniexp-client approvals rules` lists them, `rules
  remove <id>` drops one.
- **What the router sees:** the owner's public key and the current desk (machine and expiry). Not what
  is asked or answered.
- **Settings:** `Daemon:HereLifetime` (12 hours, at most 7 days).
- **New protocol types:** `approval-request`, `approval-answer`, `approval-withdrawn`.

**Done when:** tests show:
- `here` signed by the owner key is accepted and sent to every machine; one signed by another key,
  or expired, is refused by the router and by daemons;
- a request reaches the desk, and the agent gets the answer;
- an answer from a machine that isn't the desk is refused;
- with no desk a request waits, and reaches the desk once there is one; moving the desk moves it;
- "always allow" answers the next matching request at once, and only that agent and tool;
- a withdrawn request is marked so on the desk;
- the Claude Code permission tool returns `allow` with the input, or `deny` with the reason.

Live: `here` on this machine with the real Bitwarden prompt; a request from a second machine
approved here.

**As built:**
- Core: `OwnerKey` (ssh-ed25519 lines, SHA-256 fingerprints, SSHSIG preimages; verified with
  BouncyCastle and checked against a signature made by OpenSSH's `ssh-keygen -Y sign`), `SshAgent`
  (the agent protocol over the Windows pipe or `SSH_AUTH_SOCK`), `HereClaim`, the approval bodies;
  protocol 8.
- Router: `OwnerDesk` keeps the owner key and the newest claim in `router_state`; hub methods
  `GetOwner`, `GetHere`, `SetHere`, `LeaveDesk`; `HereChanged` pushed to every machine, and to a
  connecting machine before its queue. `admin owner [set <key>]`; the owner key is in backups.
- Client schema 10: `settings` (the pin, the desk), `approvals`, `approval_rules`.
- The CLI does the signing (the SSH agent is the user's, which a service may not reach); the daemon
  checks claims against its pin. An asker picks its request id, so it can withdraw even before its
  request is stored.

## Phase 10: Owner admin from anywhere (workshop, done)

Every `poniexp admin` command, run from any approved machine as `poniexp-client admin <command>`,
signed with the owner key: one SSH-agent (Bitwarden) prompt per command. `docker exec` keeps working
(bootstrap and recovery), and setting the first owner key needs it (nothing can sign before there's
a key).

- **The router's own keys:** an identity key and an encryption key, like a machine's (P-256), kept in
  its data directory (owner-only) and in backups. Machines learn them in `Hello` and pin them on first
  connect (behind the TLS pin); a different key later is refused.
- **A command:** id, the router's id, the arguments, optional input (for `restore -`), the time; the
  owner's SSH signature over all of it (namespace `poniexp`). The CLI has the SSH agent sign it, the
  daemon seals it to the router's encryption key (ephemeral ECDH + HKDF + AES-256-GCM) and sends it.
- **The router** opens it, checks the owner signature, that it names this router, that its time is
  within the clock skew and after the router started, and that its id is new; runs it exactly as
  `docker exec` would (the same admin code, output captured); and returns the exit code and output
  sealed to the asking machine's encryption key and signed by the router's identity key. Each command
  is logged with the asking machine and the owner key's fingerprint.
- **Every command works remotely,** `backup`, `restore` and `owner set` included. A backup (with the
  TLS private key) arrives sealed to the asking machine, whose CLI writes it where told. A restore
  that changes the certificate still needs a router restart.
- **Changing the owner key remotely** is a handover signed by the current key. The router keeps the
  signed handovers and sends them to every machine; daemons follow a chain that starts at their pin,
  so they re-pin without asking. A key set with `docker exec` over an existing one isn't followed:
  machines keep their pin and show that it differs.
- **Owner-signed groups:** a group set with `poniexp-client admin group set` carries the signed
  command; daemons with a pinned owner key use only groups whose signature checks against it (an
  unsigned group, set with `docker exec`, is ignored with a diagnostic there).
- **Join requests on the desk:** the router sends the desk machine the machines waiting to join (name,
  join code, notes, address), whenever that changes and when the desk moves or reconnects. They show
  in `approvals` beside agents' requests; `approve <id>` there signs and runs `admin approve`, `deny`
  runs `admin remove`. Answered elsewhere, they leave the desk.

**Done when:** tests show:
- a signed command runs and returns what `docker exec` would; an unsigned, wrongly signed, stale,
  replayed or other-router command is refused;
- the response is sealed to the asking machine and signed by the router; a machine refuses a router
  key that differs from its pin;
- backup and restore round-trip remotely;
- a remote `owner set` moves every machine's pin, offline ones when they reconnect; a `docker exec`
  one doesn't;
- a signed group is used; an unsigned one is ignored where an owner key is pinned;
- a join request reaches the desk, approving it there admits the machine, and one approved elsewhere
  leaves the desk.

Live: `admin list` and `admin approve` from this machine with the real Bitwarden prompt; a join
request approved from the desk.

**As built:** protocol 9. Core `SealedBox` (one reader, bound to a purpose), `AdminCommand`,
`SignedAdminResult`, `PendingJoin`; the router's keys in `HelloResult`. Router `RouterKeys`
(`router-keys.json`), `RemoteAdmin` (runs `AdminCli` against its own admin listener, so remote admin
needs `Router:AdminPort`; refuses `--url` and file paths), owner handovers and group signatures in
`router_state`, the keys and handovers in backups. Daemons pin the router key (client setting
`router_key`), follow handovers, check group signatures, and keep join requests as `join` approvals
(their id is the machine id). The CLI signs; `approve`/`deny` of a join runs `admin approve`/`remove`.

## Phase 11: Key rotation and revocation

- Identity key rotation, with the new key signed by the old one.
- Encryption key rotation. Old encryption private keys are kept, protected, for as long as
  envelopes encrypted to them are retained in the client log (90 days by default).
- A `machine_ids` table in the client database: this machine's past and present ids, so messages
  from before a rotation are still recognised as its own.
- Revocation signed by the owner, for a lost or stolen machine.
- What happens to messages, files and jobs waiting for a revoked machine.

## Phase 12: Web interface (workshop)

A web interface served by the router: the job board as a dashboard (phase 8), and the router's state
(machines and presence, rooms and their statistics, the traffic log, files). Read-only views first;
admin actions in it are signed with the owner key (phases 9 and 11). Open, for the workshop:
- who may open it, and how they sign in (the owner key, an approved machine, a token);
- where it listens (the relay port, the admin port, both);
- the stack (server-rendered pages, or a small static app over the router's JSON);
- live updates (polling or a push stream).

## Phase 13: Hermes bridge (workshop)

Comes after everything else, and starts with a deep dive into how Hermes handles A2A, then a design
discussion. Until then agents on Hermes use PoniExp through MCP like any other. Adds `bridge_tasks`
to the client database.

**Draft, written before the deep dive:**

The Hermes bridge pushes incoming messages into the local Hermes gateway
over A2A, since agents don't poll MCP. Hermes puts each A2A task into its live agent session,
so choosing the session is the gateway's job, not the bridge's.
- The bridge uses `message/stream` (SSE), not a blocking send, and turns the A2A task's state
  changes into receipts and replies:

  | A2A task state | PoniExp |
  |---|---|
  | `submitted` (the gateway took it) | nothing new; `received` was already sent |
  | `working` (the agent has started on it) | `acknowledged` |
  | `input-required` | a reply with `status: progress`, carrying the question |
  | `completed` | a reply with `status: done`, carrying the agent's answer |
  | `failed`, `rejected`, `canceled` | a reply with `status: failed` |

- The A2A `contextId` is derived from the peer (`machine/agent`) **and** the `conversationId`,
  so a conversation stays one multi-turn exchange inside Hermes. The sender chooses the
  `conversationId`, so using it alone would let another machine join someone else's conversation.
- The A2A message carries the PoniExp message id, sender (`machine/agent`) and conversation id
  in its metadata, so the agent can use the MCP tools on it (e.g. `reply`).
- The A2A task id is stored in `bridge_tasks`. After a restart the bridge picks the task up again
  (`tasks/subscribe`, or `tasks/get` for one that has already finished), so no receipt or reply
  is lost.
- **Answering a question:** when the sender replies to an `input-required` question (its
  `replyTo` is that progress reply), the bridge looks up the task in `bridge_tasks` and sends the
  answer into the same A2A task, rather than starting a new one. An answer is accepted only from the
  peer the task belongs to.
- The bridge sends the reply, from the task result. If the agent already replied to the same
  message through the MCP `reply` tool, the bridge doesn't send another: one answer, never two.
- The agent doesn't need to call `ack` itself: the task state is set by Hermes, so it can't be
  forgotten. `ack` remains for MCP agents that pull from their inbox.
- **To check first:** whether Hermes keeps a task `submitted` while the live session is busy with
  another turn, and only moves it to `working` when the agent actually starts on it. If it marks
  the task `working` as soon as it's injected, the bridge acknowledges on the first event the
  agent produces for the task instead.

**Found in Hermes' A2A adapter (`plugins/platforms/a2a/`), October 2026:**
- It sends `submitted` and then `working` the moment it hands a task to the session, even while the
  session is busy with another turn; nothing later says the agent has started. **Decided:**
  `acknowledged` is sent on `working`, meaning the message has been handed to the agent's session and
  will be worked on in turn.
- Every incoming message is a new task; an answer to `input-required` can't continue the task that
  asked. It can only go into the same `contextId` as a new task, linked through `bridge_tasks`.
- Tasks are kept in memory only. A bridge restart can reattach (`tasks/subscribe`, `tasks/get`) while
  the gateway is up; a gateway restart loses them. **Decided:** a message whose task is lost gets a
  reply with `status: failed` and the reason "Hermes restarted"; the sender can retry (idempotent
  through `operationId`, phase 5).
- A task the agent hasn't answered within `A2A_REPLY_TIMEOUT` (default 300 seconds, orphan ceiling
  24 hours) fails, so the bridge's setup has to raise it.
- At most `A2A_MAX_PINGPONG_TURNS` incoming messages per `contextId` (default 5, at most 20), so a long
  conversation is refused partway. **Open:** how the bridge handles it.
- The agent sees text only; data parts are rendered into it as JSON, so the PoniExp ids travel as a
  data part.
- Each conversation (`contextId`) is its own Hermes session. A gateway can also serve other profiles
  (`a2a_served_agents`), but through a one-shot `hermes chat` per turn, not a live session; hence one
  gateway per profile.
- Callers are identified by per-peer tokens (`A2A_PEER_TOKENS`); the bridge's token is one of these.
- **Done when** (draft): against a live Hermes gateway, the reply arrives once, from the task result;
  an answer to an `input-required` question lands in the same context; a bridge restart mid-task
  still delivers the acknowledgement and the reply; a gateway restart mid-task produces a `failed`
  reply; two peers using the same `conversationId` land in different contexts; and an answer to
  another peer's question is refused.

## Alongside

- **`docs/security.md`:** the threat model, updated each phase. What the server can and can't do:
  - **Can't:** read content, alter it unnoticed, substitute keys, or re-attribute content to
    another sender.
  - **Can:** see who talks to whom, when, and how much, and which recipients share a file; delay or
    drop messages (drops show up as sequence gaps in the recipient's metrics).

## Open questions

None right now; each workshop phase lists its own.

## Phase 14: Tray app (workshop; waits for Oriel)

Built on Oriel (riddlemd/Oriel: Electron for .NET on CEF), not Avalonia, once Oriel is far enough
along; last because it waits on Oriel and nothing depends on it. Oriel already has `Tray` on all
three OSes; it still needs to ship (M4: runtime packages, layouts, installers) and finish
`Notification` (Windows toasts with buttons work; Linux has no actions, macOS none yet).

A small PoniExp tray app on each machine: shows approval requests on the desk (Approve / Deny /
Always, as an Oriel window card or a native notification), the desk state (`here` / `away`), and the
daemon's health. Open, for the workshop: card or notification, how it's installed and started with
the daemon, and what else it shows.
