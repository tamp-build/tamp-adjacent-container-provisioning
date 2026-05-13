# CI pipeline templates — sidecar lifecycle across hosts

`Tamp.AdjacentContainer.Provisioning` is intentionally CI-host-agnostic. The library generates the compose YAML + env-var contract; the lifecycle (`docker compose up`, run tests, `docker compose down`) runs as Tamp targets. Below: the canonical pipeline YAML for the three hosts most adopters use. They all reduce to the same Tamp invocation — the CI YAML is thin glue.

The pattern is always:

1. Restore SDKs + Tamp tools.
2. Invoke a single Tamp target that triggers the full graph: `StartSidecars` → `IntegrationTests` → `StopSidecars` (the last marked `AssuredAfterFailure(true)`).
3. Upload test results.

The Tamp target graph handles compose lifecycle internally. The CI YAML doesn't need separate "spin up sidecars" / "tear down sidecars" steps — Tamp owns that ordering.

---

## Azure DevOps

```yaml
# azure-pipelines.yml
trigger:
  branches:
    include: [main]
  tags:
    include: ['v*']

pool:
  vmImage: ubuntu-latest

variables:
  DOTNET_NOLOGO: '1'
  DOTNET_CLI_TELEMETRY_OPTOUT: '1'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'

steps:
  - task: UseDotNet@2
    inputs:
      packageType: 'sdk'
      version: '10.0.x'

  - script: docker info
    displayName: 'Verify Docker is available'

  - script: dotnet tool restore
    displayName: 'Restore Tamp tools'

  - script: dotnet tamp Ci
    displayName: 'Tamp Ci (StartSidecars → IntegrationTests → StopSidecars)'
    env:
      ASPNETCORE_ENVIRONMENT: $(BUILD_REASON_TO_ENV)

  - task: PublishTestResults@2
    condition: always()                       # always upload, even if tests failed
    inputs:
      testResultsFormat: 'VSTest'
      testResultsFiles: '**/TestResults/**/*.trx'
      mergeTestResults: true
      failTaskOnFailedTests: true
```

**Notes for ADO self-hosted agents:**

- If your self-hosted agent runs in a Docker-in-Docker container, mount `/var/run/docker.sock` into the agent so sibling containers can be spawned. Without that mount, `docker compose up` will fail at `Cannot connect to the Docker daemon`.
- Tamp.AdjacentContainer's local-fallback path uses Testcontainers, which has the same DinD requirement — same fix applies.
- Persistent agent VMs share Docker state across builds; pin a unique `WithProjectName(...)` per Tamp project to avoid container-name collisions between concurrent builds.

---

## GitHub Actions

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push:
    branches: [main]
    tags:    ['v*']
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: ubuntu-latest
    env:
      DOTNET_NOLOGO: '1'
      DOTNET_CLI_TELEMETRY_OPTOUT: '1'
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'

    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x

      # GitHub-hosted ubuntu-latest runners have Docker preinstalled and the
      # actions runner already has access to /var/run/docker.sock. No extra setup needed.
      - run: docker info

      - run: dotnet tool restore

      - run: dotnet tamp Ci

      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: '**/TestResults/**/*.trx'
          if-no-files-found: warn
```

**Notes for GitHub Actions:**

- `macos-latest` runners do **not** ship Docker; they ship `docker` CLI but no daemon. If your sidecar tests must run on macOS, use a docker-host-aware setup (Colima, lima, or a dedicated docker-daemon runner). Most adopters run sidecar-dependent integration tests on `ubuntu-latest` only and skip them on the macOS / Windows matrix legs.
- The standard `actions/cache` for `~/.nuget/packages` works orthogonally to the sidecar story — Tamp's CI cadence isn't affected.

---

## GitLab CI

```yaml
# .gitlab-ci.yml
image: mcr.microsoft.com/dotnet/sdk:10.0

variables:
  DOTNET_NOLOGO: '1'
  DOTNET_CLI_TELEMETRY_OPTOUT: '1'
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1'

services:
  # GitLab's job runner sidecars don't replace this satellite — Tamp drives
  # docker-compose directly via the host's docker daemon. The 'services'
  # key here is unused; left empty deliberately.

stages:
  - test

integration-tests:
  stage: test
  tags:
    - docker-shell                 # runner tag that grants /var/run/docker.sock access
  before_script:
    - docker info
    - dotnet tool restore
  script:
    - dotnet tamp Ci
  artifacts:
    when: always
    reports:
      junit: '**/TestResults/**/*.trx'      # GitLab understands TRX since 16.x
    paths:
      - '**/TestResults/'
    expire_in: 7 days
```

**Notes for GitLab:**

- GitLab's `services:` block (which spins up sidecars in *separate* containers networked to the job container) is NOT the right tool here. It can't model multi-port sidecars cleanly, doesn't give you `docker compose down -v` semantics, and forces ports through GitLab's network proxy. Use the docker-shell executor (or docker-machine executor) so the Tamp job has direct access to the host's docker daemon, then let Tamp drive compose lifecycle.
- The runner tag `docker-shell` is convention, not enforced. Whatever tag your team uses to denote "this runner has docker socket access" works.
- If your shared runners don't have docker-shell access, register a self-hosted runner with `--docker-volume /var/run/docker.sock:/var/run/docker.sock` and tag it appropriately.

---

## Common configuration

Across all three hosts, the test target (in `Build.cs`) looks the same:

```csharp
[FromPath("docker")] readonly Tool Docker = null!;

AbsolutePath ComposeFile => TemporaryDirectory / "tamp-sidecars-compose.yml";

AdjacentSidecarsSpec Sidecars => AdjacentSidecars.ProvisionAll()
    .WithProjectName($"{Solution.Name}-{Git.Branch?.Replace('/', '-')}-{Git.Commit[..7]}")
    .WithPostgres(p => p.WithDatabase("integration_tests"))
    .Build();

Target StartSidecars => _ => _
    .Executes(() =>
    {
        File.WriteAllText(ComposeFile, Sidecars.ComposeYaml);
        Sidecars.ApplyToProcessEnvironment();
        return Tamp.Docker.V27.Docker.Compose.Up(s => s
            .SetFile(ComposeFile).SetProjectName(Sidecars.ProjectName)
            .SetDetach().SetWait());
    });

Target IntegrationTests => _ => _
    .DependsOn(nameof(StartSidecars))
    .Executes(() => DotNet.Test(s => s.SetProject(Solution.Path)
        .SetConfiguration(Configuration.Release)
        .SetNoBuild(false)));

Target StopSidecars => _ => _
    .AssuredAfterFailure(true)
    .DependsOn(nameof(IntegrationTests))
    .Executes(() => Tamp.Docker.V27.Docker.Compose.Down(s => s
        .SetFile(ComposeFile).SetProjectName(Sidecars.ProjectName).SetVolumes()));

Target Ci => _ => _.DependsOn(nameof(StopSidecars));
```

**The unique-project-name trick** (`$"{Solution.Name}-{Git.Branch}-{Git.Commit[..7]}"`) makes parallel builds on a shared runner safe — each gets its own compose project and never collides on container names.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `Cannot connect to the Docker daemon` during `StartSidecars` | Docker socket not accessible from the job container | Mount `/var/run/docker.sock` (ADO self-hosted, GitLab); on GitHub-hosted runners use `ubuntu-latest` not `macos-latest` |
| Postgres port conflict — `bind: address already in use` | Another container already on the host port | Override via `WithPostgres(p => p.WithHostPort(54399))` or use unique `WithProjectName` |
| Tests pass locally, fail in CI with `connection refused` | Sidecar healthcheck hadn't gone green when test started | Use `Docker.Compose.Up(s => s.SetWait())` — blocks on healthchecks before returning |
| Volume not cleaned up between builds | `docker compose down` ran without `-v` | Add `.SetVolumes()` to your `Compose.Down(...)` call |
| `StopSidecars` didn't run after test failure | Missing `AssuredAfterFailure(true)` decorator | Add it; Tamp will then run the target even when an upstream target throws |
