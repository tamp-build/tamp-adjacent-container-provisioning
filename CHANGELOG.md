# Changelog

All notable changes to **Tamp.AdjacentContainer.Provisioning** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [SemVer](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-13

### Added

- Initial release. CI-side companion to `Tamp.AdjacentContainer`. Filed under
  TAM-164. Builds deterministic docker-compose YAML for sidecar resources
  (Postgres / Azurite / Service Bus emulator) and emits the env-var contract
  that `Tamp.AdjacentContainer`'s fixture-side `AcquireAsync` reads.

#### Builder API

- **`AdjacentSidecars.ProvisionAll()`** → fluent builder.
- **`.WithProjectName(name)`** — `docker compose -p` project name. Default
  `tamp-sidecars`; override when multiple Tamp builds share an agent.
- **`.WithPostgres(p => p.WithImage(...).WithHostPort(...).WithDatabase(...))`**
  — defaults: `postgres:16-alpine` on 5432, db/user/pwd `tamp_test/tamp/tamp`.
- **`.WithAzurite(a => a.WithBlobPort(...).WithQueuePort(...).WithTablePort(...))`**
  — defaults: `mcr.microsoft.com/azure-storage/azurite:latest` on
  blob:10000 / queue:10001 / table:10002; uses Azurite's well-known
  `devstoreaccount1` credentials.
- **`.WithServiceBusEmulator(s => s.WithHostPort(...).WithConfigJson(...))`**
  — defaults: `mcr.microsoft.com/azure-messaging/servicebus-emulator:latest`
  on 5672. Optional Config.json mount for queue/topic topology.
- **`.Build()`** materializes the spec; idempotent and deterministic (same
  inputs → byte-identical YAML).

#### Spec output

- **`AdjacentSidecarsSpec.ComposeYaml`** — the `docker-compose.yml` body.
- **`AdjacentSidecarsSpec.ExportedEnvVars`** — `IReadOnlyDictionary<string,string>`
  whose keys are `TAMP_PG_CONNECTION`, `TAMP_AZURITE_CONNECTION`,
  `TAMP_SBUS_CONNECTION` — the same env-var names
  `Tamp.AdjacentContainer` reads.
- **`AdjacentSidecarsSpec.ApplyToProcessEnvironment()`** — applies
  `ExportedEnvVars` via `Environment.SetEnvironmentVariable` on the current
  process.

#### Design decision — library produces specs, adopter runs lifecycle

This library does NOT shell out to `docker compose`. Instead, adopters wire
`Tamp.Docker.V27.Docker.Compose.Up/Down` into their Tamp target graph with
`AssuredAfterFailure(true)` on the teardown target. Rationale: it composes
cleanly with Tamp's runner, produces typed `CommandPlan` values for dry-run
support, and keeps this library free of Process plumbing.

Trade-off: adopter `Build.cs` is more verbose than a `using var =` async
lifetime would be. The verbosity is bought back by full target-graph
visibility — the sidecar lifecycle appears in `tamp --list` and dry-run
output like any other target.

### Tests

- 19 unit tests covering: builder validation (no-sidecars throws, empty project
  name rejected), default project name, custom project name propagation,
  Postgres compose shape (image / env / ports / healthcheck), Postgres
  connection-string composition under default + custom configuration, Azurite
  ports + connection string with `devstoreaccount1`, Service Bus emulator
  shape + Config.json mount, three-sidecar combination, build determinism /
  idempotency, service-name override, and `ApplyToProcessEnvironment`.

### Documentation

- README with quick-start `Build.cs` snippet, verb surface, and env-var
  contract table.
- [`docs/pipelines.md`](docs/pipelines.md) (TAM-169) — canonical ADO /
  GitHub Actions / GitLab CI templates with troubleshooting tips for the
  common failure modes (docker socket not accessible, port collisions,
  missing `AssuredAfterFailure`, missing `--wait` on Compose.Up).

### Requires

- **Tamp.Core ≥ 1.6.0**. Standalone — no runtime dependency on
  `Tamp.AdjacentContainer`. Adopters can use either side without the other;
  the env-var names are duplicated as constants for the rare adopter who
  uses the provisioning side without the fixture side.

### Notes

- Originally filed 2026-05-12 alongside the AdjacentContainer 0.1.0 launch
  as the CI-side half. Lands now that the Strata pilot has matured the
  fixture-side adoption pattern.
