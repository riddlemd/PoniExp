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
- **Daemon (phase 3):** the machine's one connection; local API over a pipe or socket; endpoints with
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
  per send and shared with its recipients' machines; tickets; verified fetch shared by local endpoints;
  file receipts; release; limits with per-machine overrides; the message queue's own cap; `send/reply
  --attach`, `fetch`, `release`, `blobs`, `cancel`, `listen --auto-fetch`; MCP `attach`, `fetch`, `release`;
  `admin blobs`. Protocol 5.
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
| Addressing | A daemon per machine owns the only connection; agents are addressed as `machine/endpoint`. |
| Recipients | Every message has exactly one recipient (`machine/endpoint`). Sending to several means sending separate messages, each with its own id, sequence, envelope and log entry; no message names another recipient. A file is uploaded once and shared with all the recipients it is sent to (phase 6). |
| Not multi-tenant | A message, and the conversation it belongs to, is between one sender endpoint and one recipient endpoint. A send to several starts a separate conversation with each. Conversations with several members come with groups and rooms (phase 7). |
| Success path | A message is assumed to be going fine unless the recipient returns a failure. Replies carry `progress`, `done` or `failed`; there is no "accepted". |
| Separate tracks | Three things are tracked separately and never share a field: **delivery** of a message (receipts `received`, `acknowledged`, `rejected`), the **outcome** of the work it asked for (a reply's `status`), and **file transfer** (file receipts `downloaded`, `download-failed`). A reply's `status` is only ever about the work. |
| Message types | `type` is protocol level and hard-coded: a closed C# enum (`message`, `receipt`, `file-receipt`, `endpoint`, later `job`, `room`) that only PoniExp defines, because it decides how the daemon handles a message. Higher-level protocols between agents are not types: they ride on `type: message`, defined by its `contentType`, `metadata` and `body`, which agents are free to fill as they like. There is no reply type: a reply is a `message` with `replyTo` set. |
| Endpoint registry | Every endpoint that has ever registered with a daemon is remembered (`endpoints`). Registered but not connected is **offline**: its messages wait until it returns or they expire. Never registered, or retired with `poniexp-client endpoint retire <name>`, is **unknown**: the message gets a `rejected` receipt. |
| Automatic messages | `receipt`, `file-receipt` and `endpoint` are automatic. Nothing automatic is ever sent in response to something automatic; they expire after a short time (default 1 hour, a setting) and aren't retried after that; at most one goes out per message per event. They get rows in `messages` (so duplicates and sequence numbers work as for any message) but are never shown to agents. An expired message sends nothing back: the sender marks it `expired` by its own clock. |
| Hermes bridge | One endpoint per Hermes profile, named `hermes-<profile>` (e.g. `wojtek/hermes-default`), since each profile has its own memory, skills and gateway. Each has its own gateway URL (configuration) and its own A2A token, registered with that profile's Hermes and kept in PoniExp's secret store. |
| Daemon start-up | Two modes. **User mode** runs at logon as the user: the per-user `Run` registry key (Windows; a logon task would need admin), a LaunchAgent (macOS), a systemd user unit (Linux). **System mode** runs at boot: a Windows service under its own virtual account `NT SERVICE\PoniExp` (keys in that account's DPAPI, data in `%ProgramData%\PoniExp`, the local pipe restricted to the owning user; never a "do not store password" task), a macOS LaunchDaemon (keys in the System keychain), a systemd user unit with `loginctl enable-linger` (Linux). |
| Connection limits | By what a connection has proved, because behind port forwarding (Rancher Desktop) every client arrives from the same address. A connection that hasn't proved a key, or whose key is neither approved nor pending, must authenticate within 10 s, and at most 20 such connections exist at once. An approved or pending machine may hold at most 5 connections. At most 10 new join requests per hour across the router. Per-address limits (10 unauthenticated connections, 5 join requests an hour) apply on top, except to `SharedAddresses`: addresses known to stand for many clients, such as the port forwarder's. All of these are settings. |
| Backups | `poniexp admin backup` writes the TLS certificate (with its private key) and the approved machines to one owner-only file, or to stdout, for the owner to keep (e.g. as a Bitwarden attachment). `admin restore` puts them back. Queued messages, files and the traffic log aren't backed up. |
| Event integrity | Client events aren't hash-chained: a local process could rewrite a whole chain anyway (Local trust), and the signed envelopes already prove what was said. |
| Doing a request once | A retry is a message with the same `operationId` from the same sender endpoint. The recipient's daemon handles it, and the agent never sees a duplicate. While the first is still running (no `done` or `failed` reply yet), the daemon answers with a `progress` reply, "still running". Once it's finished, it re-sends a copy of the final reply. `operationId` defaults to the message id, so only deliberate retries are deduplicated. The record is kept for the maximum expiry plus skew. |
| Log export | `poniexp-client log --export` writes metadata only; `--bodies` decrypts bodies into it (they are then on disk in the clear). |
| Traffic log | Off by default (`poniexp admin traffic-log on`), kept 30 days. Besides what the server can see about each message (time, sender and recipient machine, id, size, sequence), it records the event (relayed, delivered, confirmed, expired, refused) and refusals with their reason (bad signature, limits, join requests), so delays and attacks show. |
| Roles | Self-declared per endpoint, carried in the signed endpoint announcements (phase 7). Phase 9 permissions may restrict them. |
| Room encryption | No room key: each room message has its own content key, wrapped for every member machine, and the router fans out one envelope. Membership changes are versioned snapshots signed (by machine key and sender endpoint) by the room's creator or the admins they name; members apply only those. New members see no earlier history. |
| Groups | Owner-defined lists of addresses on the router (`admin group`), resolved by the sender's daemon into separate messages. |
| Local trust | Every local process running as the machine's user is trusted; it could read the user's keys anyway. The daemon scopes what each endpoint is shown, but that is convenience, not a security boundary. |
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
read it through the daemon's local API. Each agent is shown only its own endpoint's messages
(scoping, not a security boundary; see Local trust). One database per machine, with endpoint
columns, not one per agent.

The phase that creates each table is in brackets. Columns are a draft; see Open questions.

`messages` (phase 1): one row per message, and so one per recipient. A send to three agents is
three messages and three rows. Written once, never edited (except dropping the envelope at the
end of its retention).
- Keyed by `(from_machine, id)`. This row is also the replay protection: a message whose key is
  already here is a duplicate. There is no separate `processed` table.
- `from_machine`, `from_endpoint`, `to_machine`, `to_endpoint`
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
  `endpoint`, `event`, `occurred_on`, `detail`
- Inbound: `stored` (`received` sent) → `fetched` → `acknowledged` (sent), or `rejected` (sent,
  with a reason) / `expired` (nothing sent; see Automatic messages).
- Outbound: `queued` → `relayed` → `received` → `acknowledged`, or `rejected` (by the recipient,
  with its reason) / `failed` (the server refused it) / `expired`. `relayed` is the server's word;
  `received`, `acknowledged` and `rejected` are the recipient's signed receipts.

`message_state` (phase 3): the furthest event per message and direction, updated in the same
transaction as the event. A late event never moves it back (a `received` after `acknowledged`), and a
final one (`rejected`, `failed`, `expired`) is never left.

`bridge_tasks` (phase 12): for messages delivered through the Hermes bridge, `message_id`,
`a2a_task_id`, `context_id`, the peer (`machine/endpoint`) the task belongs to, and the task's last
known state.

`endpoints` (phase 3): every endpoint that has registered with this daemon: `name`, `registered_on`,
`last_seen_on`, `retired_on`. The endpoint registry (see Endpoint registry).

`peer_endpoints` (phase 3): what other machines have announced: `machine`, `endpoint`, `state`
(online, left, timed out), `counter` (the announcing daemon's), `changed_on`. Each announcement is
the machine's complete list, so a newer one replaces all of that machine's rows; `peer_counters`
keeps the highest counter seen per machine, and a lower or equal one is ignored.

`daemon_state` (phase 3): this daemon's own counters (the announcement counter), which only increase.

`diagnostics` (phase 3)
- `occurred_on`, `level`, `category` (connection, signature, version, delivery), `detail`, and the
  related message or machine.

`conversations` (phase 5)
- `id`, `subject`, `local_endpoint`, `peer` (the one `machine/endpoint` on the other side),
  `started_by`, `started_on`, `last_activity_on`, `state`

`operations` (phase 5): doing a request once.
- Keyed by `(from_machine, from_endpoint, operation_id)`.
- `state` (running or done), `result_message_id`, `keep_until`.
- A retry while running gets "still running"; a retry after it's done gets the stored result.

`blobs` (phase 6): one row per blob on this machine, however many local messages refer to it.
- `blob_id`, `size`, `local_path`, `fetched_on`, `released_on` (released on the server),
  `deleted_on` (local copy removed)

`attachments` (phase 6): which message refers to which blob.
- `message_id`, `blob_id`, `name`

`attachment_events` (phase 6): append-only, the file track, separate from message delivery.
- `message_id`, `blob_id`, `endpoint`, `event`, `occurred_on`, `detail`
- Inbound: `fetched` → `verified` (`downloaded` sent) → `released` (the agent is done with it), or
  `download-failed` (sent, with a reason).
- Outbound: `uploaded` → `downloaded`, or `download-failed` (with the recipient's reason) /
  `cancelled` / `expired`. `downloaded` and `download-failed` are the recipient's signed file
  receipts.

---

## Phase 1: Message format v2 (done)

The message format changes here and again in phase 2. Nothing has joined yet, so both changes are
free.

**Addressing:** `to` and `from` are `machine/endpoint`. The server routes to the machine; the
recipient's daemon routes to the endpoint. A message has exactly one recipient; sending to several
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
| `fromEndpoint`, `toEndpoint` | The agent on each machine. |
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
`message_events`, `message_state`, `endpoints`, `peer_endpoints` and `diagnostics` to the client
database. Pushing messages into Hermes (the Hermes bridge) is phase 12.

- **`poniexp-client daemon`:** the machine's only connection to the server. It handles local
  routing to endpoints, the outbox, and the client database.
  - **Outbox:** a message sent while the router is unreachable waits in the outbox. One whose expiry
    passes before it is relayed is marked `expired` instead: its `createdOn` is signed and the
    recipient's clamp counts from it, so relaying it late would only get it refused.
- **Local API** on a Unix socket or named pipe, used by the CLI, the MCP servers and (phase 12) the
  Hermes bridge.
- **`poniexp-client mcp`** (stdio). Tools: `send`, `reply`, `ack`, `inbox`, `conversation`,
  `message` (one message and its delivery history), `machines`, `status`; later `attach` and `fetch`.
- **Endpoint announcements:** an endpoint (an MCP server, the Hermes bridge, a CLI `listen`)
  registers with its daemon by name and keeps the registration alive with a heartbeat.
  - The daemon announces each change to every approved machine: an endpoint came **online**,
    **left** (unregistered cleanly), or **timed out** (missed its heartbeat).
  - Announcements are messages of type `endpoint`, encrypted and signed like any other, one per
    machine (endpoint names are encrypted, so the server can't carry them in its presence list).
    They are automatic (see Automatic messages).
  - Each carries the announcing daemon's counter, which only ever increases. A receiver keeps only
    the newest per machine (`peer_endpoints`), so a replayed or late "left" can't override a newer
    "online".
  - When the server reports another machine coming online, the daemon sends it its full current
    endpoint list, so a machine that was away catches up.
  - When a machine goes offline, all its endpoints count as offline.
  - The MCP `machines` tool shows each machine's endpoints and their state.
  - A message for an endpoint that is offline is held by the recipient's daemon until the endpoint
    returns or the message expires. A message for an unknown endpoint (never registered, or retired
    with `poniexp-client endpoint retire <name>`) gets a `rejected` receipt ("unknown endpoint").
- **Receipts:** delivery only, each sent by the recipient as a signed, encrypted message of type
  `receipt`, so the server can't fake any of them. The outcome of the work comes back separately,
  as a reply with a `status`.
  - **`received`:** the recipient's machine has picked the message up. The daemon sends it once the
    message has passed its checks (signature, expiry, duplicate) and is stored, whether or not the
    endpoint is online.
  - **`acknowledged`:** an agent has actually taken the message on. Fetching a message isn't
    enough; the agent acknowledges it explicitly (the MCP `ack` tool, or the local API). Replying
    acknowledges too. For the Hermes bridge, see phase 12.
  - **`rejected`:** the recipient's machine won't deliver it, with a reason (e.g. "unknown
    endpoint"). Sent instead of `received`.
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
  - Tests show an endpoint's online, left and timed-out states reaching another machine, a machine
    that was offline catching up on reconnect, and a message to an unknown endpoint getting a
    `rejected` receipt, before and after it is retired.
  - Tests show a replayed or late announcement with a lower counter being ignored, a message whose
    expiry passes in the outbox being marked `expired` and never relayed, and nothing automatic sent
    in reply to something automatic.
  - Tests show `received` arriving while the recipient endpoint is offline, `acknowledged`
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

**CLI:** `poniexp-client log` by conversation, operation, peer or endpoint, with a JSONL export
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
  id, size, sequence. Endpoints are encrypted, so the log shows which machines talk, not which
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
- **Several endpoints on one machine:** if more than one local endpoint receives a message for the
  same blob, the daemon fetches it once, verifies it once and keeps one local copy.
- **File receipts:** separate from message receipts and from a reply's `status`. Sent by the
  recipient as signed, encrypted messages of type `file-receipt`, one per message that carried the
  file:
  - **`downloaded`:** the blob was fetched and verified on the recipient's machine.
  - **`download-failed`:** with a reason (chunk or hash mismatch, blob expired, cancelled by the
    sender, over a limit).
- **Local copy:** an agent is done with a file when it releases it (the MCP `release` tool or the
  local API). The local copy is deleted when every local endpoint that received it has released
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
  receipt for a tampered chunk and for a cancelled blob, one fetch for several local endpoints,
  and the local copy surviving acknowledgement and being deleted only after every local endpoint
  has released it.

## Phase 7: Rooms, roles and groups (workshop)

Decided in the workshop (see the decisions table): roles are self-declared, groups are the owner's,
rooms have no shared key.

### Roles

- A role is a tag on an **endpoint** (`reviewer`, `gpu`), same rules as an endpoint name. An endpoint
  declares its roles when it registers (`mcp --endpoint claude --role reviewer`,
  `listen --role ...`). They're kept in the endpoint registry and travel in the daemon's signed
  endpoint announcement, so the router can't invent or hide a holder. Phase 9 may restrict who can
  claim which role.
- **Addressing:** `role:reviewer` is any one holder: the sender's daemon picks an online one (at
  random), otherwise the one seen most recently, and sends it an ordinary message. `role:reviewer/all`
  is every holder: one separate message each. Neither is anything new to the router.

### Groups

- A named list of addresses (`machine/endpoint` or `role:...`), set by the owner:
  `poniexp admin group set <name> <address>...`, `group remove`, `groups`. Kept in `router.db`.
- `group:<name>` is resolved by the sender's daemon into separate messages, one per member (roles
  expanded as above). A group isn't a room: its members don't see each other.
- The router serves the lists, so a compromised router could point a group at another approved
  machine (it still can't read what it relays). Owner-signed lists can come with phase 9.

### Rooms

- **A room** is a standing, many-sided conversation: an id, a name, member endpoints and admins.
  Its history is the room's messages, kept on each member's machine (the client database), not on
  the router.
- **Membership** is a full, versioned snapshot (`version`, `name`, `members`, `admins`) in a `room`
  message, sent to the daemon of every machine with a member (and of members just removed). Its
  creator is its first admin. A machine applies a snapshot only if its version is newer and it comes
  from an admin of the room as that machine knows it (the envelope's machine signature and its
  sender endpoint), so the router can't add anyone. `room create`, `room add`, `room remove`,
  `room admin`, `room leave` (a member removing themselves), `room close`.
- **Messages:** one envelope per room message, addressed to `room:<id>` (signed, like every header
  field). Its content key is wrapped for each member machine, per message: no room key, so no
  re-keying. Removing a member means they're left out from the next message. The router fans the
  envelope out to every machine it's wrapped for and queues one entry per recipient until each
  confirms. Sequence numbers count per sender and room.
- A machine accepts a room message only from a current member, for a room it knows. A new member sees
  messages from when their machine learned of the change: no history from before they joined. A
  message sent by someone whose machine hadn't heard of the change yet may miss them; that's by design.
- **Reading:** room messages go live to every local member endpoint's stream and are read with
  `room read <id> [--since]` or the MCP `room` tool. There are no delivery receipts in rooms (that
  would be members x members automatic messages) and no inbox state: a room is read like a channel.
  Replies thread by `replyTo`; the room id is the conversation id.
- **CLI / MCP:** `room create|add|remove|admin|leave|close|list|read|send`; MCP `rooms`, `room`
  (read), `room_send`.

**Done when:** tests show role addressing (any one and all) from announcements, a group resolved into
separate messages, a room message read by every member and no one else, a removed member left out of
the next message, a membership change from a non-admin (or one the router forged) ignored, and a new
member not seeing earlier messages.

## Phase 8: Job board (workshop)

Columns: backlog → ready → claimed → running → done / failed.
- Claims are time-limited leases kept alive by heartbeat. Exactly one machine can claim a job.
- Jobs are matched to workers by role. Results come back as replies and files.

Open:
- how a job is encrypted when its claimant isn't known yet: to everyone holding the role, or with a
  key for the board
- how you see the board
- the relationship to GitHub issues (e.g. the Oriel backlog)
- whether the board survives losing the volume

## Phase 9: Permissions and approval cards (workshop)

- Deny by default, Cotal-style: who may send what to which machine, room or role, and who may post
  or claim which jobs.
- **Approval cards** are part of the permission logic: a request outside the rules is held, and a
  card goes to wherever you're active, approved with Touch ID using the owner key.
- This phase introduces the owner identity.

## Phase 10: Owner admin from anywhere

Admin commands (approve a machine, change rules, clear a backlog) signed with the owner key and sent
from any machine, instead of `docker exec` on Wojtek.

## Phase 11: Key rotation and revocation

- Identity key rotation, with the new key signed by the old one.
- Encryption key rotation. Old encryption private keys are kept, protected, for as long as
  envelopes encrypted to them are retained in the client log (90 days by default).
- A `machine_ids` table in the client database: this machine's past and present ids, so messages
  from before a rotation are still recognised as its own.
- Revocation signed by the owner, for a lost or stolen machine.
- What happens to messages, files and jobs waiting for a revoked machine.

## Phase 12: Hermes bridge (workshop)

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

- The A2A `contextId` is derived from the peer (`machine/endpoint`) **and** the `conversationId`,
  so a conversation stays one multi-turn exchange inside Hermes. The sender chooses the
  `conversationId`, so using it alone would let another machine join someone else's conversation.
- The A2A message carries the PoniExp message id, sender (`machine/endpoint`) and conversation id
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

1. **Backups of the job board:** decide in phase 8.
