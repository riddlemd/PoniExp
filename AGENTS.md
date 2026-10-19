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
```

## Executables

- `poniexp` (`src/PoniExp.Server`): server-side, one subcommand per function: `router`, `admin`. New server functionality is a new subcommand, not a new executable.
- `poniexp-client` (`src/PoniExp.Cli`): what each machine runs.
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
  "Acknowledge" is reserved for an agent taking a message on (phase 3). Don't mix them.
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
- Registry and queue writes are serialised with a `SemaphoreSlim`, saved to disk first, then
  published as a new immutable snapshot. Reads don't lock.
- Every `Task`-returning method ends in `Async`.
- Timestamps are named `*On` / `*_on`, never `*At` / `*_at`.
- No secrets in the repo: keys live in `~/.poniexp/` and the router's `/data` volume.
