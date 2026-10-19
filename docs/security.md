# PoniExp threat model

What each party can and can't do, as built. Updated each phase (see `docs/plan.md`).

**Current state: phase 4** (router on SQLite, connection and join limits, backups).

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
Endpoints, conversation, subject, type, metadata, body and `createdOn` are encrypted.

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

- Delivery receipts (`received`, `acknowledged`, `rejected`) and endpoint announcements are messages:
  signed by the machine that sends them and encrypted, so the router can't fake, read or alter one.
  It can drop or delay them like any message.
- A receipt counts only from the machine the message was sent to: one claiming to be about a message
  sent elsewhere is ignored (and recorded in diagnostics).
- Each announcement carries the announcing daemon's counter, which only increases. A receiver keeps
  the newest list per machine, so a replayed or late announcement can't bring back an old state.
- Endpoint names travel encrypted inside announcements: the router doesn't learn which agents a
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

## The local API

- HTTP over a named pipe (Windows) or a Unix socket, never a TCP port. The pipe is the current
  user's only; the socket is mode 0600 in the (0700) home directory. In system mode on Windows the
  service's pipe admits only the service and the user who installed it.
- Inside a machine, endpoints don't authenticate to the daemon: every local process of the user is
  trusted (see Parties). The daemon shows each endpoint only its own messages, for convenience, not
  as a security boundary.

## Machines

- A member machine can only send as itself: the router checks the envelope's sender against the key
  the connection proved, and the recipient checks the signature again.
- Any approved machine can message any endpoint on any other (permissions are phase 9).
- Private keys are kept in the OS's secret store: DPAPI (current user) on Windows, the login keychain
  on macOS, owner-only (0600) files on Linux, where disk encryption protects them at rest.
- In system mode (a boot-time service) the daemon has its own keys, under its own account (Windows:
  `NT SERVICE\PoniExp`, DPAPI; macOS: root). It is a separate machine identity, joined separately.
- The client database (`~/.poniexp/client.db`) keeps messages as their encrypted envelopes. Routing and
  conversation metadata (endpoints, subject, type, ids, times) is in the clear there for searching;
  bodies are not.
- Not yet: rotating or revoking keys (phase 11). A stolen machine's keys stay valid until it is removed.
