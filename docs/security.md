# PoniExp threat model

What each party can and can't do, as built. Updated each phase (see `docs/plan.md`).

**Current state: phase 2** (end-to-end encryption).

## Parties

- **Machines**, each holding two private keys that never leave it: an identity key (ECDSA P-256; the
  machine id is a hash of its public key) and an encryption key (ECDH P-256), signed by the identity key.
- **The router** (`poniexp-server`), trusted to route and queue, **not** to be honest about content,
  keys, timing or completeness.
- **The owner**, who approves machines with `docker exec` on the router's host.
- **Local processes** running as a machine's user are trusted: they could read its keys anyway.

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

## Network

- TLS with a self-signed certificate pinned by SHA-256 on every client: no public CA, and a man in the
  middle is refused.

## Machines

- A member machine can only send as itself: the router checks the envelope's sender against the key
  the connection proved, and the recipient checks the signature again.
- Any approved machine can message any endpoint on any other (permissions are phase 9).
- Private keys are kept in the OS's secret store: DPAPI (current user) on Windows, the login keychain
  on macOS, owner-only (0600) files on Linux, where disk encryption protects them at rest.
- The client database (`~/.poniexp/client.db`) keeps messages as their encrypted envelopes. Routing and
  conversation metadata (endpoints, subject, type, ids, times) is in the clear there for searching;
  bodies are not.
- Not yet: rotating or revoking keys (phase 11). A stolen machine's keys stay valid until it is removed.
