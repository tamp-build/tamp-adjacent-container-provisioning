# Tamp.AdjacentContainer.Provisioning

> CI-side companion to [`Tamp.AdjacentContainer`](https://github.com/tamp-build/tamp-adjacent-container). Builds a deterministic `docker-compose.yml` for sidecar resources (Postgres, Azurite, Service Bus emulator) and emits the env-var contract that `Tamp.AdjacentContainer`'s fixture-side code reads. Adopters wire `docker compose up/down` through [`Tamp.Docker.V27`](https://github.com/tamp-build/tamp-docker)'s `Compose` facade into target dependencies + `AssuredAfterFailure` cleanup.

| Package | Status |
|---|---|
| `Tamp.AdjacentContainer.Provisioning` | 0.1.0 (initial) |

## Why this exists

`Tamp.AdjacentContainer` solves the fixture-side problem ("my integration tests need a real Postgres — discover it from env or fall back to Testcontainers"). What it doesn't solve: **getting the sidecar up before the test process runs, and torn down after, in a Tamp `Build.cs` target graph**.

Today adopters either:

1. **Hand-write `docker-compose.yml`** + parallel `docker compose up -d` shell steps in their CI YAML. Duplicates per adopter, drifts from the `Tamp.AdjacentContainer` env-var contract over time.
2. **Use Testcontainers' local-fallback path even in CI**. Works but spawns a fresh container per test class, hammering image-pull bandwidth and skipping the "shared sidecar across all test classes in this run" pattern the adjacent-container pattern was designed for.

This satellite generates the YAML deterministically + emits the env-var contract as a typed dictionary. The Tamp target graph runs the compose lifecycle via `Tamp.Docker.V27.Docker.Compose.Up/Down`.

## Install

```bash
dotnet add package Tamp.AdjacentContainer.Provisioning
```

Multi-targets net8 / net9 / net10. Requires `Tamp.Core` ≥ 1.6.0. Standalone — no `Tamp.AdjacentContainer` dependency (the env-var contract is duplicated as constants so adopters can use one without the other).

## Quick start — Postgres-only sidecar for an integration-test target

```csharp
using Tamp;
using Tamp.AdjacentContainer.Provisioning;
using Tamp.Docker.V27;

class Build : TampBuild
{
    public static int Main(string[] args) => Execute<Build>(args);

    [FromPath("docker")] readonly Tool Docker = null!;

    AbsolutePath ComposeFile => TemporaryDirectory / "tamp-sidecars-compose.yml";

    // Compute the spec once at build time — same inputs → byte-identical YAML, deterministic.
    AdjacentSidecarsSpec Sidecars => AdjacentSidecars.ProvisionAll()
        .WithProjectName("strata-it-pool")
        .WithPostgres(p => p.WithDatabase("strata_it"))
        .Build();

    Target StartSidecars => _ => _
        .Description("[Test] docker compose up -d sidecars defined by the Sidecars spec.")
        .Executes(() =>
        {
            File.WriteAllText(ComposeFile, Sidecars.ComposeYaml);
            Sidecars.ApplyToProcessEnvironment();   // exports TAMP_PG_CONNECTION on the current process
            return Tamp.Docker.V27.Docker.Compose.Up(s => s
                .SetFile(ComposeFile)
                .SetProjectName(Sidecars.ProjectName)
                .SetDetach()
                .SetWait());                        // wait for healthchecks to go healthy
        });

    Target IntegrationTests => _ => _
        .DependsOn(nameof(StartSidecars))
        .Executes(() => DotNet.Test(s => s.SetProject(SolutionPath).SetConfiguration(Configuration.Release)));

    Target StopSidecars => _ => _
        .AssuredAfterFailure(true)                  // ← runs even if IntegrationTests fails
        .DependsOn(nameof(IntegrationTests))
        .Description("[Test] docker compose down -v sidecars.")
        .Executes(() => Tamp.Docker.V27.Docker.Compose.Down(s => s
            .SetFile(ComposeFile)
            .SetProjectName(Sidecars.ProjectName)
            .SetVolumes()));
}
```

The shape: **`StartSidecars → IntegrationTests → StopSidecars`** with `StopSidecars` marked `AssuredAfterFailure(true)` so cleanup happens whether tests pass or fail. Pure target-graph composition; no library-managed `IAsyncDisposable` lifecycle.

## Verb surface

### Builder

```csharp
var spec = AdjacentSidecars.ProvisionAll()
    .WithProjectName("optional-explicit-name")
    .WithPostgres(p => p
        .WithImage("postgres:16-alpine")
        .WithHostPort(5432)
        .WithDatabase("tamp_test")
        .WithUsername("tamp")
        .WithPassword("tamp")
        .WithServiceName("postgres"))
    .WithAzurite(a => a
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithBlobPort(10000)
        .WithQueuePort(10001)
        .WithTablePort(10002))
    .WithServiceBusEmulator(s => s
        .WithImage("mcr.microsoft.com/azure-messaging/servicebus-emulator:latest")
        .WithHostPort(5672)
        .WithConfigJson("./sbus-config.json"))
    .Build();
```

`Build()` validates that at least one sidecar is configured. Same inputs always produce byte-identical YAML.

### Spec

```csharp
spec.ProjectName        // string — used by docker compose -p
spec.ComposeYaml        // string — write to disk before docker compose up
spec.ExportedEnvVars    // IReadOnlyDictionary<string, string>
spec.ApplyToProcessEnvironment()    // applies ExportedEnvVars via Environment.SetEnvironmentVariable
```

### Env-var contract

The `ExportedEnvVars` dictionary contains exactly what `Tamp.AdjacentContainer`'s fixture-side `AcquireAsync` reads:

| Resource | Key | Value shape |
|---|---|---|
| Postgres | `TAMP_PG_CONNECTION` | `Host=localhost;Port={port};Database={db};Username={u};Password={p}` |
| Azurite | `TAMP_AZURITE_CONNECTION` | full Azure Storage connection string with `devstoreaccount1` defaults |
| Service Bus emulator | `TAMP_SBUS_CONNECTION` | `Endpoint=sb://localhost:{port}/;SharedAccessKeyName=RootManageSharedAccessKey;...UseDevelopmentEmulator=true;` |

When `Tamp.AdjacentContainer.Postgres.AdjacentPostgres.AcquireAsync(...)` runs, it reads `TAMP_PG_CONNECTION` and returns a `TampConnection` with `Mode = Adjacent`. The provisioning side and the fixture side share the env-var contract as the integration point.

## CI pipeline templates

See [docs/pipelines.md](docs/pipelines.md) for canonical ADO / GitHub Actions / GitLab pipeline shapes — they all reduce to the same pattern: `Tamp` target named `StartSidecars` runs `docker compose up`, then the test target, then a target marked `AssuredAfterFailure(true)` runs `docker compose down -v`.

## What's NOT in 0.1.0

- **Dynamic port allocation.** Default ports are pinned (5432 for Postgres, 10000-10002 for Azurite, 5672 for Service Bus). CI agents typically have nothing else on those ports; adopters who do override per-resource via `WithHostPort` / `WithBlobPort` / etc. If multiple parallel Tamp builds share an agent, use unique `WithProjectName` AND override ports to avoid collision.
- **Healthcheck waiting from within the library.** The compose YAML declares healthchecks for every service; pair with `Docker.Compose.Up(... .SetWait())` to block on them. The library doesn't poll docker itself — that's the Docker.V27 wrapper's job.
- **Per-test-class isolation.** Sidecars are shared across all test classes in a Tamp build run. Test code uses `IClassFixture<T>` (xUnit) or equivalent to share connections; reset state via `DROP SCHEMA` per test class. See [`Tamp.AdjacentContainer` README → Schema state in adjacent mode](https://github.com/tamp-build/tamp-adjacent-container#schema-state-in-adjacent-mode).

## Pairs with

- [`Tamp.AdjacentContainer`](https://github.com/tamp-build/tamp-adjacent-container) — fixture-side companion. Reads the env vars this satellite exports.
- [`Tamp.Docker.V27`](https://github.com/tamp-build/tamp-docker) — provides `Docker.Compose.Up / Down` for the lifecycle invocation.
- [`Tamp.EFCore.V8/V9/V10`](https://github.com/tamp-build/tamp-ef) — typical post-sidecar test setup runs migrations against the provisioned Postgres.

## Releasing

Releases follow the [Tamp dogfood pattern](MAINTAINERS.md).

## License

MIT. See [LICENSE](LICENSE).
