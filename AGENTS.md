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

- **Every signature goes through `SigningPayload.Build(purpose, fields...)`.** It length-prefixes
  fields and puts the purpose first, so one signature can't be replayed as another kind. New signed
  messages get a new purpose string and a method in `Protocol/Signatures.cs`.
- **Signed timestamps are truncated to milliseconds** before signing (`Signatures.Truncate`), so
  they survive JSON round trips.
- **Hub method names cross a string boundary.** Use the `HubMethods` constants on both sides (with
  `[HubMethodName]` on the hub); never a literal.
- **Client `On` handlers only enqueue work** (`Post`). Awaiting a hub call inside a handler blocks
  the receive loop that would complete it.
- **The router trusts nothing the client says about itself** except what a signature proves: the
  sender id comes from the authenticated connection, and is checked against the envelope.
- **Admin is loopback-only** (`docker exec`). Don't expose it on the relay port.
- Registry and queue writes are serialised with a `SemaphoreSlim`, saved to disk first, then
  published as a new immutable snapshot. Reads don't lock.
- Every `Task`-returning method ends in `Async`.
- No secrets in the repo: keys live in `~/.poniexp/` and the router's `/data` volume.
