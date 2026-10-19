# PoniExp plan

The roadmap from a working relay to a coordination service for agents on several machines: what is
built, what each phase adds, what has been decided, and what is still open.

Every phase lands as its own commits. A phase is done when its tests pass three runs in a row and
it has been checked live against the `poniexp-server` container. Phases marked **workshop** start
with a design discussion before any code.

## Built

- **Identity:** an ECDSA P-256 key per machine; the machine id is a hash of its public key.
- **Joining:** a signed join request (public key, name, description), compared by join code and
  approved with `docker exec poniexp-server poniexp admin approve`.
- **Connecting:** a signed challenge on every connect; only approved keys can send, receive or list
  machines.
- **Messages:** signed by the sender, checked by the server and again by the recipient.
- **Delivery:** queued for offline machines (7 days), kept until the recipient acknowledges them.
- **Presence:** who's online, pushed to every machine.
- **TLS:** a self-signed certificate made once by the server and pinned by clients.
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
| Storage | SQLite, bundled in the app (`Microsoft.Data.Sqlite`). Nothing for the user to install. |
| Join requests | Expire after a configurable time. |
| Relay data | Short-lived by design. What, if anything, outlives the volume is still open (see Open questions). |
| File limits | A quota per sender **and** a cap per recipient, plus limits per file and for the whole relay. No per-sender share of a recipient's cap. Files are something machines fetch deliberately; normal messages must always get through. |
| Naming | Timestamps end in `On` / `_on` (`createdOn`, `expires_on`); never `At` / `_at`. |
| Deferred | CI. Owner admin from anywhere, and key rotation and revocation, come after everything else. |

---

## Phase 1: Message format v2

The message format changes here and again in phase 3. Nothing has joined yet, so both changes are
free.

**Addressing:** `to` and `from` are `machine/endpoint`. The server routes to the machine; the
recipient's daemon routes to the endpoint.

**Visible to the server** (needed for routing and expiry):

| Field | Purpose |
|---|---|
| `id` | Unique per message (the nonce). |
| `fromMachine`, `toMachine` | Routing. |
| `expiresOn` | The server stops delivering after this. |
| `sequence` | Per sender → recipient machine; lets the recipient detect messages the server dropped. |
| `size` | Length of the encrypted payload. |

**Encrypted in phase 3:**

| Field | Purpose |
|---|---|
| `fromEndpoint`, `toEndpoint` | The agent on each machine. |
| `conversationId` | Groups an exchange into a thread; replies inherit it. |
| `replyTo` | The message id this answers. |
| `operationId` | The same across retries of one action, so the recipient does it once. |
| `status` | For replies: `accepted`, `progress`, `done`, `failed`. |
| `subject` | A readable title for conversation lists. |
| `kind`, `contentType` | What the message is, and how to read the body. |
| `metadata` | A small key/value map. |
| `body`, `createdOn` | The content, and the sender's timestamp. |

In phase 1 the second group is signed but not yet encrypted. The split is fixed now so the format
changes only once more.

**Also in this phase:**
- A protocol version check at connect. Incompatible clients get a clear "upgrade" refusal.
- Rename existing `*At` members (`CreatedAt`, `RequestedAt`, `DecidedAt`, `QueuedAt`, `LastSeen`,
  `PresenceChange.At`) to `*On`.

**Done when:**
- `SendAsync` and `ReplyAsync` take the new fields, and the CLI can set them (`--reply-to`,
  `--expires 10m`, `--subject`, ...).
- Tests show:
  - the signature covers every field
  - an expired message isn't delivered
  - a gap in sequence numbers is reported
  - a mismatched protocol version is refused

## Phase 2: Server foundations

- **SQLite** replaces the JSON files: machines, queue, and later files and jobs.
- **Hardening before the port faces the internet:**
  - connection and join limits per IP address
  - join requests that expire after a configurable time
  - `poniexp admin clear-pending`
- **`/health` endpoint** and a Docker health check.
- **Backups:** depends on the open question below.

## Phase 3: Encryption

- **An encryption key per machine:** created next to the identity key, signed by it, and sent in the
  join request (required). The server distributes it; clients check the signature before encrypting
  to it, so the server can't substitute its own.
- **Bodies and the encrypted fields** from phase 1 are encrypted to the recipient: a random content
  key, AES-256-GCM, with the key wrapped per recipient through ECDH P-256 + HKDF-SHA256. The sender's
  signature covers the encrypted bytes.
- **Keys are stored securely:**
  - Windows: DPAPI.
  - macOS: the Keychain.
  - Linux: owner-only files.

  This follows KHost's secret-store approach.
- **Done when:**
  - a server-substituted key is rejected by the client
  - messages on the server's disk are unreadable
  - a tampered message fails to decrypt

## Phase 4: Logs

### Client log

One SQLite database per machine. Only the daemon writes to it; agents read it through the daemon's
local API. Each agent sees only its own endpoint's messages.

What it's for:
- history
- conversation memory for agents after a restart
- replay protection
- doing a request once
- inbox state for each agent
- outbox and receipts
- diagnostics

**Draft structure (still under discussion):**

`messages`: written once, never edited.
- `id`, `from_machine`, `from_endpoint`, `to_machine`, `to_endpoint`
- `conversation_id`, `reply_to`, `operation_id`, `kind`, `status`, `content_type`, `subject`,
  `sequence`
- `created_on` (sender, signed), `relayed_on` (server), `stored_on` (local), `expires_on`
- `envelope`: the original signed, encrypted message, so the signature can be re-checked later
- `body`: decrypted, and protected at rest

There's no `direction` column. Direction depends on which endpoint is looking, and is derived by
comparing `from_machine` against the machine's id history. Indexes on `from_machine` and
`to_machine`.

`message_events`: append-only.
- `message_id`, `endpoint`, `event`, `occurred_on`, `detail`
- Inbound: `stored` → `delivered` → `read` → `handled`, or `rejected` / `expired`.
- Outbound: `queued` → `relayed` → `received` → `read` → `replied`, or `failed` / `expired`.

`message_state`: the latest event per message and endpoint, updated in the same transaction as the
event.

`conversations`
- `id`, `subject`, `participants`, `started_by`, `started_on`, `last_activity_on`, `state`

`processed`: replay protection.
- Keyed by `(from_machine, message_id)`, with `processed_on` and `keep_until`.
- Rows are kept until the server could no longer deliver the message, and never deleted early.

`operations`: doing a request once.
- Keyed by `(from_machine, from_endpoint, operation_id)`.
- `state` (running or done), `result_message_id`, `keep_until`.
- A retry while running gets "still running"; a retry after it's done gets the stored result.

`attachments`
- `message_id`, `blob_id`, `name`, `size`, `local_path`, `fetched_on`, `released_on`

`diagnostics`
- `occurred_on`, `level`, `category` (connection, signature, version, delivery), `detail`, and the
  related message or machine.

`machine_ids`: this machine's past and present ids, so messages from before a key rotation are
still recognised as its own.

**Retention, set per kind:**

| What | Default |
|---|---|
| `processed`, `operations` | Until they can no longer matter: server retention + longest expiry + clock skew. |
| Message bodies | 90 days, then the body is dropped and the metadata kept. |
| Metadata and events | 1 year. |
| Diagnostics | 30 days. |
| Size cap | e.g. 1 GB; the oldest bodies are dropped first. |

**CLI:** `poniexp-client log` by conversation, operation or peer, with a JSONL export.

### Server traffic log

- Off by default; `poniexp admin traffic-log on|off`, with its own retention.
- Records only what the server can see: `occurred_on`, sender machine, recipient machine, message
  id, size, sequence. Endpoints are encrypted, so the log shows which machines talk, not which
  agents.

## Phase 5: Daemon and MCP

- **`poniexp-client daemon`:** the machine's only connection to the server. It handles local
  routing to endpoints, the outbox, and the client log.
- **Local API** on a Unix socket or named pipe, used by the CLI, the MCP servers and the Hermes
  bridge.
- **`poniexp-client mcp`** (stdio). Tools: `send`, `reply`, `inbox`, `conversation`, `machines`,
  `status`; later `attach` and `fetch`.
- **Hermes delivery:** pushes incoming messages into the local Hermes gateway on localhost, since
  agents don't poll MCP. Where it delivers (the current session or a dedicated one) is decided in
  this phase.
- **Read receipts:** sent when a message reaches `read`.
- **`poniexp-client doctor`:** checks reachability, the certificate pin, clock skew, membership
  and the daemon.
- **Self-contained builds** for Windows, macOS and Linux, with no .NET install needed. The bundled
  SQLite library must unpack correctly from the single-file build; test it on each OS.
- **Running as a service:** the daemon starts automatically on each machine.
- **Done when:** Hermes on one machine, through MCP, holds a conversation with an agent on another.

## Phase 6: Encrypted files

- **Encryption:**
  - A random key per upload.
  - 4 MB chunks, each encrypted with AES-256-GCM. The chunk index and a last-chunk flag are bound
    into each chunk, so the server can't reorder, drop or truncate them unnoticed.
  - An encrypted manifest holding the filename, content type and real size.
  - The file key is wrapped per recipient, and for the sender.
  - The hash of the chunk-hash list is signed into the message.
- **Transfer:**
  1. `BeginUpload` returns a blob id and a short-lived ticket covering one blob and one direction.
  2. Chunks go up with `PUT /blobs/{id}/chunks/{n}`; `GET /blobs/{id}/status` shows what's
     there, so uploads can resume.
  3. `CompleteUpload` seals the blob.
  4. A recipient gets a download ticket from `RequestDownload` (only granted to recipients), then
     fetches chunks with `GET /blobs/{id}/chunks/{n}`.
- **Storage:**
  - Each upload is its own blob, stored under a random id. Nothing is shared between conversations,
    because that leaks information and lets someone fetch a file they never had.
  - A blob is readable only by its message's sender and recipients.
- **Deletion:**
  - Each recipient releases the blob after a verified download.
  - The blob is deleted when the last recipient releases it, after 7 days, or when the sender
    cancels it.
  - Unsealed uploads are dropped after 24 hours.
- **Limits** (configurable, with overrides per machine):

  | Limit | Default |
  |---|---|
  | Per file | 2 GB |
  | Per sender | 10 GB, counting a broadcast once |
  | Per recipient | 5 GB, counting a broadcast in full against each recipient |
  | Whole relay | 50 GB, plus a 5 GB free-disk floor |

  - Space is reserved when an upload starts.
  - Refusals say why ("ise has 4.8 of 5 GB waiting, offline since ...").
- **Message queue:** its own cap (about 100 MB queued per recipient), separate from files, so normal
  messages always get through.
- **CLI:**
  - `send --attach`, `fetch`, `listen --auto-fetch`
  - `blobs` and `cancel` (sender)
  - `poniexp admin blobs [clear --for <machine>]`

## Phase 7: Rooms, roles and groups (workshop)

Starting definitions:
- **Group:** a named list of machines.
- **Role:** a capability tag, e.g. `gpu` or `reviewer`, addressed as "any one with it" or "all".
- **Room:** a standing conversation with members and history.

Open:
- who assigns roles
- room history and encryption: a shared room key, and re-keying when membership changes
- how rooms relate to conversations

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
- Revocation signed by the owner, for a lost or stolen machine.
- What happens to messages, files and jobs waiting for a revoked machine.

## Alongside

- **`docs/security.md`:** the threat model, updated each phase. What the server can and can't do:
  - **Can't:** read content, alter it unnoticed, or substitute keys.
  - **Can:** see who talks to whom, when, and how much; delay or drop messages (sequence numbers
    reveal the drops).

## Open questions

1. **Log structure:** review the draft above before phase 4, in particular:
   - Protecting bodies at rest: encrypt with a local key in DPAPI or the Keychain (recommended; no
     searching inside the text), or leave them readable and rely on disk encryption (allows
     full-text search).
   - When a message counts as "read", and so sends a receipt: when it's fetched, or only when the
     agent marks it read.
   - One log per machine with an endpoint column (recommended), or one per agent.
   - Whether to chain events by hash so edits show (leaning no).
   - Whether the traffic log's fields cover what it's needed for.
2. **Backups:** relay data is meant to be short-lived. Proposal:
   - **Back up:** the TLS certificate (losing it breaks every client's pin) and the approved-machine
     list (losing it means re-approving everyone), via `poniexp admin backup`.
   - **Don't back up:** queued messages, files and the traffic log.
   - **Decide in phase 8:** the job board.
3. **Reaching port 5443 from away:** a router port forward or the VPN. Needed before Ise joins;
   phase 2's hardening comes first.
