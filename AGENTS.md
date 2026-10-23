# AGENTS.md

**PoniExp**: secure relay for agents across machines. .NET 10, solution `PoniExp.slnx`.

## Commands

```bash
dotnet build PoniExp.slnx
dotnet test PoniExp.slnx                       # starts real routers on random loopback ports
docker compose up -d --build                   # the router, in a Linux container
docker exec poniexp-server poniexp admin list    # admin, inside the container
dotnet run --project src/PoniExp.Server -- router   # run the server locally
dotnet run --project src/PoniExp.Cli -- <command>     # poniexp-client
scripts/publish.sh win-x64 linux-x64                  # self-contained single-file clients in artifacts/
node --test integrations/hermes/tests/poniexp.test.mjs  # Hermes TUI widget (plain JS, no build)
```

## Executables

- `poniexp` (`src/PoniExp.Server`): server-side, one subcommand per function: `router`, `admin`. New server functionality is a new subcommand, not a new executable.
- `poniexp-client` (`src/PoniExp.Cli`): what each machine runs. `poniexp-client daemon` is the
  machine's connection; the other commands, and `poniexp-client mcp`, talk to it over the local API.
- The container is `poniexp-server`; its entrypoint is `poniexp router`.

## Rules

- **The design is in `docs/plan.md`** (decisions, phases, open questions) and the threat model in
  `docs/security.md`. Read the decisions before changing the protocol.
- **Every signature goes through `SigningPayload.Build(purpose, fields...)`.** It length-prefixes
  fields and puts the purpose first, so one signature can't be replayed as another kind. New signed
  messages get a new purpose string and a method in `Protocol/Signatures.cs`.
- **Signed timestamps are truncated to milliseconds** before signing (`Signatures.Truncate`), so
  they survive JSON round trips.
- **Hub method names cross a string boundary.** Use the `HubMethods` constants on both sides (with
  `[HubMethodName]` on the hub); never a literal.
- **Client `On` handlers only enqueue work** (`Post`). Awaiting a hub call inside a handler blocks
  the receive loop that would complete it.
- **The message format is versioned.** Any change to `Envelope`, `MessageContent` or a hub method's
  shape bumps `WireProtocol.Version`; mismatched clients are refused at `Hello`.
- **The signature covers every field.** A new envelope or content field goes into
  `Signatures.EnvelopePayload` / `ContentPayload` and gets a line in the "covers every field" test.
- **`MessageType` is a closed enum** for protocol-level handling only. Agents' own protocols use
  `type: message` with `contentType`, `metadata` and `body`; there is no reply type (`replyTo` says it).
- **"Confirm" is the client telling the router it stored a message** (`HubMethods.Confirm`).
  "Acknowledge" is an agent taking a message on (the `acknowledged` receipt). Don't mix them.
- **Everything sent goes through the outbox:** `RelayClient.Compose(tx, ...)` inside a
  `ClientStore.Transact`, then `FlushOutboxAsync`. Whatever a message causes (its event, a receipt, an
  announcement) is composed in the transaction that records the cause, so it is sent exactly once
  even across a crash. Never call the hub's `Send` directly.
- **Automatic messages** (`receipt`, `file-receipt`, `agent`) are the daemon's: never answered by
  anything automatic, short expiry (`Daemon:AutomaticExpiry`), never shown to agents.
- **`RelayClient.IsConnected` means authenticated on this connection,** not just a live socket: the
  router refuses anything sent between a reconnect and re-authentication.
- **The local API is HTTP over a named pipe / Unix socket** (`LocalAddress.ForHome`). On Windows a
  pipe client must not wait for a pipe that doesn't exist (check `\\.\pipe\<name>` first, connect
  with a timeout), or a stopped daemon hangs its callers.
- **Encryption goes through `MessageCrypto` / `Signatures.SealEnvelope`.** The router-visible header
  is the AES-GCM associated data and the message id, sender and reader are in the HKDF info; keep it
  that way for any new sealed thing. Never encrypt to a key that hasn't passed
  `PublicKeys.VerifyEncryptionKey` against the machine's identity key.
- **Private keys only go through `PoniExp.Secrets` (`KeyRing`)**, never a plain file.
  `src/PoniExp.Secrets/Interop/` is ported third-party code kept close to upstream (see its `UPSTREAM`
  file): don't reformat it.
- **Every check that protects against the router also runs on the recipient** (signature, expiry
  by its own clock, duplicates): the router is trusted to route, not to be honest.
- **Tunable values are settings,** with defaults, in an options class validated at startup.
  Singletons read `IOptionsMonitor<T>.CurrentValue` where they use it; the hub (scoped) uses
  `IOptionsSnapshot<T>`. Listener, TLS and data-directory settings are startup-only.
- **The router trusts nothing the client says about itself** except what a signature proves: the
  sender id comes from the authenticated connection, and is checked against the envelope.
- **Admin is loopback-only** (`docker exec`). Don't expose it on the relay port.
- The client database (`ClientStore`, SQLite) is shared by every process on the machine: writes are
  short IMMEDIATE transactions. Store a message before confirming it to the router.
- Registry and queue writes are serialised with a `SemaphoreSlim`, saved to the router's SQLite
  database (`RouterStore`, only the rows that changed) first, then published as a new immutable
  snapshot. Reads don't lock.
- **Connection limits go by what a connection proved** (`ConnectionGate`), never by address alone:
  behind port forwarding every client has the same address.
- Tests that restart a router on the same data directory set `RouterFixture.KeepData` on the first.
- Every `Task`-returning method ends in `Async`.
- Timestamps are named `*On` / `*_on`, never `*At` / `*_at`.
- No secrets in the repo: keys live in `~/.poniexp/` and the router's `/data` volume, and backups
  (which hold the router's private key) stay out of it too.

- Client schema changes go through `ClientStore` (`SchemaVersion` and `CREATE ... IF NOT EXISTS`);
  retention must never touch a live message (`queued`, `stored`, `fetched`).
