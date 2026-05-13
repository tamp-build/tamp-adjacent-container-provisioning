namespace Tamp.AdjacentContainer.Provisioning;

/// <summary>
/// CI-side companion to <c>Tamp.AdjacentContainer</c>. Builds a deterministic
/// <c>docker-compose</c> YAML for sidecar resources (Postgres, Azurite,
/// Service Bus emulator) and computes the env-var contract that
/// <c>Tamp.AdjacentContainer</c>'s fixture-side code reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Design choice — library produces specs, adopter runs lifecycle.</b>
/// This library does NOT shell out to <c>docker compose</c>. Instead it produces
/// an <see cref="AdjacentSidecarsSpec"/> containing the YAML and the env-var
/// dictionary, and the adopter wires <c>docker compose up / down</c> through
/// <see cref="Tamp"/>'s existing facade (<c>Tamp.Docker.V27.Docker.Compose</c>)
/// into target-graph dependencies + <c>AssuredAfterFailure</c> cleanup. The
/// resulting Build.cs is more verbose but composes cleanly with Tamp's runner
/// and produces typed <see cref="CommandPlan"/> objects for dry-run support.
/// </para>
/// <para>
/// <b>Ports are explicit, not dynamic.</b> CI agents typically have nothing else
/// on the standard sidecar ports (5432 for Postgres, 10000-10002 for Azurite,
/// 5672 for Service Bus emulator), so the default behavior is to pin the
/// canonical port. Adopters with port conflicts override per-resource
/// (<c>WithPostgres(p =&gt; p.WithHostPort(54399))</c>).
/// </para>
/// </remarks>
public static class AdjacentSidecars
{
    /// <summary>Start building a sidecar-provisioning spec.</summary>
    public static AdjacentSidecarsBuilder ProvisionAll() => new();
}

/// <summary>Fluent builder for a sidecar set.</summary>
public sealed class AdjacentSidecarsBuilder
{
    private string _projectName = "tamp-sidecars";
    private PostgresSidecar? _postgres;
    private AzuriteSidecar? _azurite;
    private ServiceBusEmulatorSidecar? _serviceBus;

    /// <summary>
    /// Override the <c>docker compose</c> project name (default <c>tamp-sidecars</c>).
    /// Pin a unique name when multiple Tamp builds run on the same agent so their
    /// sidecar containers don't collide.
    /// </summary>
    public AdjacentSidecarsBuilder WithProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("project name must not be empty.", nameof(name));
        _projectName = name;
        return this;
    }

    /// <summary>Provision a Postgres sidecar. Default image <c>postgres:16-alpine</c>, port <c>5432</c>, db/user/pwd <c>tamp_test/tamp/tamp</c>.</summary>
    public AdjacentSidecarsBuilder WithPostgres(Action<PostgresSidecar>? configure = null)
    {
        _postgres = new PostgresSidecar();
        configure?.Invoke(_postgres);
        return this;
    }

    /// <summary>Provision an Azurite sidecar (Azure Storage emulator). Default image <c>mcr.microsoft.com/azure-storage/azurite:latest</c>, blob:10000 / queue:10001 / table:10002.</summary>
    public AdjacentSidecarsBuilder WithAzurite(Action<AzuriteSidecar>? configure = null)
    {
        _azurite = new AzuriteSidecar();
        configure?.Invoke(_azurite);
        return this;
    }

    /// <summary>Provision a Service Bus emulator sidecar. Default image <c>mcr.microsoft.com/azure-messaging/servicebus-emulator:latest</c>, port <c>5672</c>.</summary>
    public AdjacentSidecarsBuilder WithServiceBusEmulator(Action<ServiceBusEmulatorSidecar>? configure = null)
    {
        _serviceBus = new ServiceBusEmulatorSidecar();
        configure?.Invoke(_serviceBus);
        return this;
    }

    /// <summary>
    /// Materialize the spec. Idempotent — call repeatedly to get byte-identical YAML and
    /// env-var dictionaries (useful for "did the compose change?" diffing in CI).
    /// </summary>
    public AdjacentSidecarsSpec Build()
    {
        if (_postgres is null && _azurite is null && _serviceBus is null)
            throw new InvalidOperationException(
                "At least one sidecar must be configured (call WithPostgres / WithAzurite / WithServiceBusEmulator).");

        var yaml = ComposeWriter.Write(_projectName, _postgres, _azurite, _serviceBus);
        var env = EnvWriter.Build(_postgres, _azurite, _serviceBus);
        return new AdjacentSidecarsSpec(_projectName, yaml, env);
    }
}

/// <summary>
/// Materialized output of the builder. Holds the project name, the compose YAML, and the
/// dictionary of env vars to export before <c>Tamp.AdjacentContainer</c>'s fixture-side
/// <c>AcquireAsync</c> runs.
/// </summary>
public sealed class AdjacentSidecarsSpec
{
    internal AdjacentSidecarsSpec(string projectName, string composeYaml, IReadOnlyDictionary<string, string> exportedEnvVars)
    {
        ProjectName = projectName;
        ComposeYaml = composeYaml;
        ExportedEnvVars = exportedEnvVars;
    }

    /// <summary>The <c>docker compose -p &lt;name&gt;</c> project name. Pin to a unique value when running multiple Tamp builds on the same agent.</summary>
    public string ProjectName { get; }

    /// <summary>The generated <c>docker-compose.yml</c> content. Write to disk before invoking <c>docker compose up</c>.</summary>
    public string ComposeYaml { get; }

    /// <summary>
    /// Environment variables to export on the test process. Keys match the contract
    /// <c>Tamp.AdjacentContainer</c> reads (<c>TAMP_PG_CONNECTION</c>,
    /// <c>TAMP_AZURITE_CONNECTION</c>, <c>TAMP_SBUS_CONNECTION</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> ExportedEnvVars { get; }

    /// <summary>
    /// Apply <see cref="ExportedEnvVars"/> to the current process via
    /// <see cref="Environment.SetEnvironmentVariable(string, string?)"/>. Useful when the Tamp
    /// build target runs in-process and the test runner immediately follows.
    /// </summary>
    public AdjacentSidecarsSpec ApplyToProcessEnvironment()
    {
        foreach (var (k, v) in ExportedEnvVars)
            Environment.SetEnvironmentVariable(k, v);
        return this;
    }
}

// ────────────────────────────────────────────────────────────────────────────
//  Per-sidecar options classes
// ────────────────────────────────────────────────────────────────────────────

/// <summary>Postgres sidecar configuration. Default <c>postgres:16-alpine</c> on port 5432.</summary>
public sealed class PostgresSidecar
{
    public string Image { get; set; } = "postgres:16-alpine";
    public int HostPort { get; set; } = 5432;
    public string Database { get; set; } = "tamp_test";
    public string Username { get; set; } = "tamp";
    public string Password { get; set; } = "tamp";
    public string ServiceName { get; set; } = "postgres";

    public PostgresSidecar WithImage(string image) { Image = image; return this; }
    public PostgresSidecar WithHostPort(int port) { HostPort = port; return this; }
    public PostgresSidecar WithDatabase(string name) { Database = name; return this; }
    public PostgresSidecar WithUsername(string user) { Username = user; return this; }
    public PostgresSidecar WithPassword(string pwd) { Password = pwd; return this; }
    public PostgresSidecar WithServiceName(string name) { ServiceName = name; return this; }

    internal string ConnectionString() =>
        $"Host=localhost;Port={HostPort};Database={Database};Username={Username};Password={Password}";
}

/// <summary>Azurite sidecar configuration. Default <c>mcr.microsoft.com/azure-storage/azurite:latest</c>, blob:10000 / queue:10001 / table:10002.</summary>
public sealed class AzuriteSidecar
{
    public string Image { get; set; } = "mcr.microsoft.com/azure-storage/azurite:latest";
    public int BlobPort { get; set; } = 10000;
    public int QueuePort { get; set; } = 10001;
    public int TablePort { get; set; } = 10002;
    public string ServiceName { get; set; } = "azurite";

    public AzuriteSidecar WithImage(string image) { Image = image; return this; }
    public AzuriteSidecar WithBlobPort(int port) { BlobPort = port; return this; }
    public AzuriteSidecar WithQueuePort(int port) { QueuePort = port; return this; }
    public AzuriteSidecar WithTablePort(int port) { TablePort = port; return this; }
    public AzuriteSidecar WithServiceName(string name) { ServiceName = name; return this; }

    // Azurite's default account is well-known and documented in the image:
    // https://learn.microsoft.com/en-us/azure/storage/common/storage-use-azurite#well-known-storage-account-and-key
    internal const string DefaultAccountName = "devstoreaccount1";
    internal const string DefaultAccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    internal string ConnectionString() =>
        $"DefaultEndpointsProtocol=http;" +
        $"AccountName={DefaultAccountName};" +
        $"AccountKey={DefaultAccountKey};" +
        $"BlobEndpoint=http://localhost:{BlobPort}/{DefaultAccountName};" +
        $"QueueEndpoint=http://localhost:{QueuePort}/{DefaultAccountName};" +
        $"TableEndpoint=http://localhost:{TablePort}/{DefaultAccountName};";
}

/// <summary>Service Bus emulator sidecar. Default <c>mcr.microsoft.com/azure-messaging/servicebus-emulator:latest</c> on port 5672.</summary>
public sealed class ServiceBusEmulatorSidecar
{
    public string Image { get; set; } = "mcr.microsoft.com/azure-messaging/servicebus-emulator:latest";
    public int HostPort { get; set; } = 5672;
    public string ServiceName { get; set; } = "servicebus";
    /// <summary>
    /// Optional path to a Service Bus emulator config file (<c>Config.json</c>). When set,
    /// the compose YAML mounts it into the container at <c>/ServiceBus_Emulator/ConfigFiles/Config.json</c>.
    /// </summary>
    public string? ConfigJsonPath { get; set; }
    public string EmulatorAcceptEula { get; set; } = "Y";

    public ServiceBusEmulatorSidecar WithImage(string image) { Image = image; return this; }
    public ServiceBusEmulatorSidecar WithHostPort(int port) { HostPort = port; return this; }
    public ServiceBusEmulatorSidecar WithServiceName(string name) { ServiceName = name; return this; }
    public ServiceBusEmulatorSidecar WithConfigJson(string path) { ConfigJsonPath = path; return this; }

    internal string ConnectionString() =>
        $"Endpoint=sb://localhost:{HostPort}/;" +
        $"SharedAccessKeyName=RootManageSharedAccessKey;" +
        $"SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
}
