# PoniExp threat model

What each party can and can't do, as built. Updated each phase (see `docs/plan.md`).

**Current state: phase 1** (message format v2). Messages are signed but **not yet encrypted**: the
router can read their content. End-to-end encryption is phase 2.

## Parties

- **Machines**, each holding an ECDSA P-256 identity key that never leaves it. The machine id is a
  hash of the public key.
- **The router** (`poniexp-server`), trusted to route and queue, **not** to be honest about content,
  timing or completeness.
- **The owner**, who approves machines with `docker exec` on the router's host.
- **Local processes** running as a machine's user are trusted: they could read its key anyway.

## The router

**Can't:**
- Forge a message, or alter any field of one unnoticed: the sender signs every field (header and
  content) and the recipient verifies against the sender's key, not the router's word.
- Replay a message: the recipient keeps every message id it has stored (keyed by sender machine and
  id) and drops a repeat.
- Stretch a message's life: the recipient treats a message as expiring at most `MaxExpiry` after its
  signed `createdOn`, by its own clock, and refuses anything expired or created in the future.
- Join or approve machines: only a connection that proves an approved key can send or receive.

**Can:**
- **Read message content** (until phase 2).
- See who talks to whom, when, and how much.
- Delay messages, or drop them. Drops show up as gaps in the per-sender sequence numbers, counted in
  the recipient's metrics; a gap from a message that legitimately expired looks the same.
- Lie about when it received a message (`relayedOn` is its unsigned word).

## Network

- TLS with a self-signed certificate pinned by SHA-256 on every client: no public CA, and a man in the
  middle is refused.

## Machines

- A member machine can only send as itself: the router checks the envelope's sender against the key
  the connection proved, and the recipient checks the signature again.
- Any approved machine can message any endpoint on any other (permissions are phase 9).
- Each machine's client database (`~/.poniexp/client.db`) holds message content in the clear until
  phase 2 stores encrypted envelopes; it is owner-only on Unix.
