# Contributing to PoniExp

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Before you start

- For anything bigger than a small fix, open an issue or a
  [discussion](https://github.com/riddlemd/PoniExp/discussions) first, so we can agree on the approach.
- Read the rules in [`AGENTS.md`](AGENTS.md) (signing, wire-protocol versioning, hub method names,
  the outbox). They apply to human contributors too.
- Protocol changes: read the decisions in [`docs/plan.md`](docs/plan.md) and the threat model in
  [`docs/security.md`](docs/security.md) first.
- Security problems go through [`SECURITY.md`](SECURITY.md), not a public issue.

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json`).

```sh
dotnet build PoniExp.slnx
dotnet test PoniExp.slnx                                # starts real routers on random loopback ports
dotnet run --project src/PoniExp.Server -- router        # run the router locally
dotnet run --project src/PoniExp.Cli -- <command>        # poniexp-client
node --test integrations/hermes/tests/*.test.mjs         # Hermes TUI widget (plain JS, no build)
```

Warnings are errors (`TreatWarningsAsErrors`), so the build must be clean.

> [!NOTE]
> On macOS, the three tests that start an HTTPS router fail with `PlatformNotSupportedException`
> (`EphemeralKeySet`). CI runs on Linux.

## Making a change

1. Fork the repo and create a branch from `main`.
2. Make your change, with tests.
3. Run `dotnet build` and `dotnet test` locally.
4. Open a pull request against `main` and fill in the template. CI must pass.

## Tests

Tests are xUnit and are written **Given / When / Then**: the name says the scenario, and the body
has the three sections marked.

```csharp
[Fact]
public async Task Given_AnOfflineMachine_When_AMessageIsSent_Then_ItIsQueued()
{
    // Given
    ...

    // When
    ...

    // Then
    ...
}
```

Prefer end-to-end tests against a real router and real daemons (`RouterFixture`, `DaemonFixture`)
over mocks. Every `Task`-returning method, tests included, ends in `Async`.

## Code style

- Comments say **why**, not what, in a line or two.
- Tunable values are settings with defaults, validated at startup.
- Keep `src/PoniExp.Secrets/Interop/` close to upstream: don't reformat it.

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
