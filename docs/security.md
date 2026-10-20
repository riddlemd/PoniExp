# PoniExp threat model

What each party can and can't do, as built. Updated each phase (see `docs/plan.md`).

**Current state: phase 8** (the job board).

## Parties

- **Machines**, each holding two private keys that never leave it: an identity key (ECDSA P-256; the
  machine id is a hash of its public key) and an encryption key (ECDH P-256), signed by the identity key.
- **The router** (`poniexp-server`), trusted to route and queue, **not** to be honest about content,
  keys, timing or completeness.
- **The owner**, who approves machines with `docker exec` on the router's host.
- **Local processes** running as a machine's user are trusted: they could read its keys anyway.
- **The daemon** on each machine: the only process that talks to the router. Agents reach it through
  its local API.

## What the router sees

Only what routing needs: message id, sender and recipient machine, `expiresOn`, sequence number,
ciphertext size, and which machines the content key is wrapped for (the recipient and the sender).
Agents, conversation, subject, type, metadata, body and `createdOn` are encrypted.

## The router

**Can't:**
- Read content: it is sealed with a random per-message key (AES-256-GCM), wrapped for each reader with
  ephemeral-static ECDH P-256 and HKDF-SHA256. What it stores on disk is ciphertext.
- Substitute keys: each machine's encryption key is signed by its identity key, and senders check that
  signature before encrypting. A key the router made up is refused.
- Forge a message, or alter any field of one unnoticed: the sender signs the header, the ciphertext and
  every key wrap, and the recipient verifies against the sender's key.
- Re-attribute content: the header is the AES-GCM associated data and the sender is in the HKDF info,
  so ciphertext re-signed by another machine fails to decrypt.
- Replay a message: the recipient keeps every message id it has stored and drops a repeat.
- Stretch a message's life: the recipient treats a message as expiring at most `MaxExpiry` after its
  signed `createdOn`, by its own clock, and refuses anything expired or created in the future.
- Join or approve machines: only a connection that proves an approved key can send or receive.

**Can:**
- See who talks to whom, when, and how much.
- Delay messages, or drop them. Drops show up as gaps in the per-sender sequence numbers, counted in
  the recipient's metrics; a gap from a message that legitimately expired looks the same.
- Lie about when it received a message (`relayedOn` is its unsigned word).
- Refuse service, e.g. by withholding a machine's keys.

## Receipts and announcements

- Delivery receipts (`received`, `acknowledged`, `rejected`) and agent announcements are messages:
  signed by the machine that sends them and encrypted, so the router can't fake, read or alter one.
  It can drop or delay them like any message.
- A receipt counts only from the machine the message was sent to: one claiming to be about a message
  sent elsewhere is ignored (and recorded in diagnostics).
- Each announcement carries the announcing daemon's counter, which only increases. A receiver keeps
  the newest list per machine, so a replayed or late announcement can't bring back an old state.
- Agent names travel encrypted inside announcements: the router doesn't learn which agents a
  machine has.

## Limits on the relay port

- A connection must prove a key that is approved or pending within `AuthenticationTimeout` (10 s), or
  it is closed; at most `MaxUnauthenticatedConnections` (20) may be waiting at once. An approved or
  pending machine may hold `MaxConnectionsPerMachine` (5) connections. Kestrel caps open connections
  (200) underneath.
- At most `JoinRequestsPerHour` (10) new join requests an hour, router-wide. A flood can therefore
  hold up genuine joins for up to an hour (and fill the pending list, `MaxPendingMachines`), but
  can't get anyone approved: approval stays a human comparing join codes. `admin clear-pending` and
  the 24-hour `JoinRequestExpiry` clear it.
- Per-address limits (10 waiting connections, 5 join requests an hour) apply on top, except to
  `SharedAddresses`. Behind Rancher Desktop's port forwarding every client has the same address, so
  only the router-wide limits protect the port there.
- `/health` says only `healthy` or `unhealthy`: no counts or names.

## Backups

- `admin backup` holds the TLS certificate's private key: whoever has it can impersonate the router to
  every pinned client (though not read messages, which are encrypted end to end, nor forge them). It
  is written owner-only, or to stdout for the owner to store (e.g. in Bitwarden).
- `admin restore` checks that the certificate matches its SHA-256 and that each machine's id is its key's
  fingerprint, and restores approved machines only.

## Network

- TLS with a self-signed certificate pinned by SHA-256 on every client: no public CA, and a man in the
  middle is refused.

## Groups and rooms

- **Groups** are the owner's lists, served by the router: a compromised router could point a group
  at another approved machine (still encrypted to that machine, so only it could read). Owner-signed
  lists can come with owner admin (phase 10).
- **Rooms:**
  - **What the router sees:** a room's name and members (which agents, when they joined and left,
    who invited whom), the machines each message goes to, and message sizes and times. Never subjects,
    bodies or which agent sent a message. That is by choice: the router enforces unique names, checks
    each message is sealed for exactly the members' machines, and collapses empty or idle rooms.
  - **What it can't do:** every membership event is signed by its author's machine, and each daemon
    checks the whole log from the room's creation (an invite only from a member, a join only by the
    invitee of an open invite, a leave only by a member). The router can't add a member, forge an
    invite or join, or read a message. It can withhold events, or refuse to relay; a daemon only keeps
    a log that extends the one it has, so it can't roll one back either.
  - **Removed members:** each message's key is wrapped for the members' machines at the time, and the
    router refuses one wrapped for anyone else, so someone who left can't read what follows. Their
    machine also drops room messages from agents that weren't members when they wrote them.
  - **Agents are labels:** the router and members know a member as `machine/agent`, vouched for by the
    machine. Any process on a member's machine can use that agent name (local processes are trusted).
  - **Removed machines:** the owner removing a machine adds an unsigned router event taking its agents
    out; members accept it only for a machine no longer in the router's approved list.
  - **After a room collapses** the router keeps statistics (members with join and leave times, counts and
    sizes per machine, each message's time and size) for `RoomStatsRetention`; each member machine keeps
    its own log and messages under its usual retention.

## The owner and approvals

- **The owner key** is an ssh-ed25519 key held by the owner's SSH agent (Bitwarden); PoniExp never
  holds the private key. The router stores the public key; **each machine pins it** (compare the
  fingerprint, or pinned without asking when that machine's SSH agent holds the key). A router that
  swaps the key can't move the desk on machines that pinned the real one.
- **The desk** (`here`) is a claim signed by the owner key: this machine, from, until (at most 7
  days), as an SSH signature in the `poniexp` namespace, so it can't be mistaken for an SSH login.
  Daemons accept only a newer claim than the last one they took. The desk machine may end its own
  desk without a signature; daemons accept the router's unsigned "away" because it can only stop
  approvals, never grant one.
- **Requests and answers** are messages between daemons, end-to-end encrypted and signed like any
  other. A daemon accepts an answer only from the machine the current desk claim names, for a request
  it sent there. The router sees that approval traffic flows, not what is asked.
- **What an approval proves:** that someone at the desk machine said yes. A process on the desk
  machine could answer too (local processes are trusted); the Bitwarden prompt guards making a machine
  the desk, not each answer.
- **"Always allow" rules** live on the asking machine and match an agent, a tool and (for a command)
  the exact command. Agent names are labels, so a rule for one agent can be used by any process on
  that machine that claims its name.

## Owner admin from anywhere

- **The router has its own keys** (identity and encryption, P-256), in `router-keys.json` (owner-only)
  and in backups. Machines pin the identity key on first connect, behind the TLS pin, and refuse a
  router presenting another one.
- **A remote admin command** is signed by the owner key (covering its id, the router's id, the time,
  the arguments and a hash of any input) and sealed to the router's encryption key. The router runs it
  only if the signature checks, it names this router, its time is within the clock skew and after the
  router started, and its id hasn't run; it refuses `--url` and file paths. The result is sealed to the
  asking machine and signed by the router. TLS already protects the link; the router's keys add
  protection that doesn't depend on the certificate (a TLS-terminating proxy, a restored certificate)
  and let machines check that a result came from the router.
- **Who can admin:** whoever holds the owner key. A compromised machine without it can't.
- **Owner key changes** made remotely are handovers signed by the key before; machines follow the chain
  from their own pin. A key set with `docker exec` over an existing one isn't followed.
- **Signed groups:** once an owner key is pinned, daemons use only groups whose `group set` the owner
  signed (with the current or an earlier owner key), so the router can't add members. It could still
  serve an older signed version of a group, or hide one.
- **Join requests** reach the desk from the router; approving one is an owner-signed `admin approve`.

## Signed-only changes, rotation and revocation

- **Once there's an owner key, every admin change is owner-signed.** The router's admin listener
  refuses a change that doesn't carry its in-process token (only a run of a signed command does), so a
  `docker exec` change waits for the desk to sign exactly it. With no owner key everything runs
  unsigned (a new router); `owner set --lost-key` is the unsigned way back from a lost key, and
  machines don't follow it. Whoever controls the router's container can still change its data
  directly; machines guard against that with what they check themselves (pins, signed groups,
  signed revocations).
- **Encryption keys rotate** (90 days). Each is signed with its issue time and machines take only
  newer ones, so a router can't hand back an old (perhaps leaked) key. Retired private keys are kept
  for the envelope retention, then deleted.
- **Identity keys don't rotate.** A machine whose identity key may have leaked is revoked; it rejoins
  with new keys (`keys reset`) as a new machine.
- **Revocation** is owner-signed and enforced by every machine, which keeps the list itself. What a
  revoked machine already received stays readable to whoever holds it.

## The job board

- **Not end-to-end encrypted, by design.** The board is an open forum for every approved machine:
  the router stores jobs (titles, descriptions, comments, result notes, who posted and claimed them)
  in the clear, and they are in its backups. Only approved machines can read or change it (over TLS).
  Don't put secrets on the board; send them as messages, which stay encrypted.
- **Every change is signed** by the acting machine (its key, whose fingerprint is its id), with a
  timestamp within the clock skew; the router refuses a change signed for another machine or altered.
  The router itself could still rewrite the board, since it holds it in the clear.
- **Agents are labels:** a machine vouches for its agents.
- **Requirements are checked against what each machine reports** of itself (detected by its daemon):
  the router enforces them on claims, but a dishonest approved machine could report hardware it
  doesn't have.
- **Leases** are renewed by the claimant's daemon while that agent is online; a claim nobody renews
  returns to `ready` after `JobLease`.

## Files

- **What the router sees of a file:** a random blob id, who uploaded it and which machines it is shared
  with (it routes their messages anyway), the number and sizes of its chunks (so the file's size, give
  or take 16 bytes a chunk), and when each machine fetches it. Not its name, type or contents: those
  are in the encrypted manifest and chunks.
- **Keys:** a random 256-bit key per upload, carried only in the messages (encrypted to the recipient
  and the sender, and signed). Chunks are AES-256-GCM with the chunk's index as nonce and the blob id,
  index and a last-chunk flag as associated data, so chunks can't be reordered, swapped between blobs,
  dropped or truncated unnoticed. The manifest lists every chunk's hash, and the message pins that
  list by its hash, so the router can't swap in another blob even one sealed with the same key.
- **Who may read it:** download tickets go only to the sender and the machines named at upload. A
  machine it isn't shared with is told "no such blob", never who it is shared with. Tickets are random,
  for one blob and one direction, and last 15 minutes.
- **No deduplication:** each upload is its own blob, so the router can't tell two uploads are the same
  file, and having a blob's id lets no one fetch it.
- **Local copies:** written to `<home>/files/<blob>/` only after verification (a `.part` file
  until then), and deleted when every agent that received them has released them, or after 90 days.
- **Limits:** space is reserved when an upload starts, against the file, sender, recipient and relay
  limits and a free-disk floor, so a full disk or one machine's backlog can't stop messages, which
  have their own cap per recipient (100 MB queued).
- In system mode the daemon reads files to send, and writes fetched ones, as the service's account.

## Logs

- **Client log:** the client database keeps envelopes, not decrypted bodies, so `log` shows
  metadata (machines, agents, conversation, operation, subject, status, times and states) unless
  `--bodies` decrypts them. A `--bodies` export is a plaintext file: it's written owner-only (on
  Unix; on Windows it inherits the folder's ACL), and keeping it safe is up to its owner.
- Events aren't hash-chained: a local process could rewrite the whole chain anyway (see Parties).
  What a peer said is proved by its signed envelope, kept for 90 days by default.
- **Retries:** a retry is matched by `(sender machine, sender agent, operationId)`, all of them
  signed by the sender, so one machine can't answer, or block, another's operation. Records are kept
  for MaxExpiry plus ClockSkew, as long as a message can be accepted, so a replay is a duplicate,
  not a new request.
- **Traffic log:** off by default. When on, the router records what it already sees (machines, id,
  size, sequence, times) and, for refusals and join requests, the connection's address and the
  reason. It never has agents, subjects or bodies. It's kept 30 days (`TrafficLogRetention`).

## The local API

- HTTP over a named pipe (Windows) or a Unix socket, never a TCP port. The pipe is the current
  user's only; the socket is mode 0600 in the (0700) home directory. In system mode on Windows the
  service's pipe admits only the service and the user who installed it.
- Inside a machine, agents don't authenticate to the daemon: every local process of the user is
  trusted (see Parties). The daemon shows each agent only its own messages, for convenience, not
  as a security boundary.

## Machines

- A member machine can only send as itself: the router checks the envelope's sender against the key
  the connection proved, and the recipient checks the signature again.
- Any approved machine can message any agent on any other: there are no machine-to-machine rules.
  Approvals (phase 9) are how agents ask before acting. Agent
  names are never a security boundary (any local process can use any name), so agents that must not
  impersonate each other belong on separate machines or containers.
- Private keys are kept in the OS's secret store: DPAPI (current user) on Windows, the login keychain
  on macOS, owner-only (0600) files on Linux, where disk encryption protects them at rest.
- In system mode (a boot-time service) the daemon has its own keys, under its own account (Windows:
  `NT SERVICE\PoniExp`, DPAPI; macOS: root). It is a separate machine identity, joined separately.
- The client database (`~/.poniexp/client.db`) keeps messages as their encrypted envelopes. Routing and
  conversation metadata (agents, subject, type, ids, times) is in the clear there for searching;
  bodies are not.
- Not yet: rotating or revoking keys (phase 11). A stolen machine's keys stay valid until it is removed.
