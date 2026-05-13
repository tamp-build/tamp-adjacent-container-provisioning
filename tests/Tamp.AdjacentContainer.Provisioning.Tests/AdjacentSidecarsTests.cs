using System;
using System.Linq;
using Tamp.AdjacentContainer.Provisioning;
using Xunit;

namespace Tamp.AdjacentContainer.Provisioning.Tests;

public sealed class AdjacentSidecarsTests
{
    // ─── Builder validation ───────────────────────────────────────────────

    [Fact]
    public void Build_With_No_Sidecars_Throws()
    {
        var b = AdjacentSidecars.ProvisionAll();
        Assert.Throws<InvalidOperationException>(() => b.Build());
    }

    [Fact]
    public void Empty_Project_Name_Rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            AdjacentSidecars.ProvisionAll().WithProjectName(""));
    }

    [Fact]
    public void Default_Project_Name_Is_Tamp_Sidecars()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithPostgres().Build();
        Assert.Equal("tamp-sidecars", spec.ProjectName);
        Assert.Contains("name: tamp-sidecars", spec.ComposeYaml);
    }

    [Fact]
    public void Custom_Project_Name_Propagates()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithProjectName("strata-test-pool-9")
            .WithPostgres()
            .Build();
        Assert.Equal("strata-test-pool-9", spec.ProjectName);
        Assert.Contains("name: strata-test-pool-9", spec.ComposeYaml);
    }

    // ─── Postgres ─────────────────────────────────────────────────────────

    [Fact]
    public void Postgres_Default_Compose_Shape()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithPostgres().Build();
        var y = spec.ComposeYaml;
        Assert.Contains("postgres:", y);
        Assert.Contains("image: postgres:16-alpine", y);
        Assert.Contains("POSTGRES_DB: tamp_test", y);
        Assert.Contains("POSTGRES_USER: tamp", y);
        Assert.Contains("POSTGRES_PASSWORD: tamp", y);
        Assert.Contains("\"5432:5432\"", y);
        Assert.Contains("pg_isready", y);
    }

    [Fact]
    public void Postgres_Connection_String_Matches_Compose()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithPostgres().Build();
        var conn = spec.ExportedEnvVars["TAMP_PG_CONNECTION"];
        Assert.Equal("Host=localhost;Port=5432;Database=tamp_test;Username=tamp;Password=tamp", conn);
    }

    [Fact]
    public void Postgres_Custom_Port_And_Database()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithPostgres(p => p.WithHostPort(54399).WithDatabase("strata_dev").WithUsername("strata").WithPassword("p!"))
            .Build();
        Assert.Contains("\"54399:5432\"", spec.ComposeYaml);
        Assert.Contains("POSTGRES_DB: strata_dev", spec.ComposeYaml);
        Assert.Contains("POSTGRES_USER: strata", spec.ComposeYaml);
        Assert.Equal("Host=localhost;Port=54399;Database=strata_dev;Username=strata;Password=p!",
            spec.ExportedEnvVars["TAMP_PG_CONNECTION"]);
    }

    [Fact]
    public void Postgres_Custom_Image()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithPostgres(p => p.WithImage("postgres:17-alpine"))
            .Build();
        Assert.Contains("image: postgres:17-alpine", spec.ComposeYaml);
    }

    // ─── Azurite ──────────────────────────────────────────────────────────

    [Fact]
    public void Azurite_Default_Compose_Shape()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithAzurite().Build();
        var y = spec.ComposeYaml;
        Assert.Contains("azurite:", y);
        Assert.Contains("mcr.microsoft.com/azure-storage/azurite", y);
        Assert.Contains("\"10000:10000\"", y);
        Assert.Contains("\"10001:10001\"", y);
        Assert.Contains("\"10002:10002\"", y);
    }

    [Fact]
    public void Azurite_Connection_String_Has_DevStoreAccount1()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithAzurite().Build();
        var conn = spec.ExportedEnvVars["TAMP_AZURITE_CONNECTION"];
        Assert.Contains("AccountName=devstoreaccount1", conn);
        Assert.Contains("BlobEndpoint=http://localhost:10000/devstoreaccount1", conn);
        Assert.Contains("QueueEndpoint=http://localhost:10001/devstoreaccount1", conn);
        Assert.Contains("TableEndpoint=http://localhost:10002/devstoreaccount1", conn);
    }

    [Fact]
    public void Azurite_Custom_Ports_Propagate_To_Connection_String()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithAzurite(a => a.WithBlobPort(11000).WithQueuePort(11001).WithTablePort(11002))
            .Build();
        Assert.Contains("BlobEndpoint=http://localhost:11000/", spec.ExportedEnvVars["TAMP_AZURITE_CONNECTION"]);
        Assert.Contains("QueueEndpoint=http://localhost:11001/", spec.ExportedEnvVars["TAMP_AZURITE_CONNECTION"]);
        Assert.Contains("TableEndpoint=http://localhost:11002/", spec.ExportedEnvVars["TAMP_AZURITE_CONNECTION"]);
    }

    // ─── Service Bus emulator ─────────────────────────────────────────────

    [Fact]
    public void ServiceBus_Default_Compose_Shape()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithServiceBusEmulator().Build();
        var y = spec.ComposeYaml;
        Assert.Contains("servicebus:", y);
        Assert.Contains("servicebus-emulator", y);
        Assert.Contains("\"5672:5672\"", y);
        Assert.Contains("ACCEPT_EULA: Y", y);
    }

    [Fact]
    public void ServiceBus_With_Config_Json_Mounts_It()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithServiceBusEmulator(s => s.WithConfigJson("./Config.json"))
            .Build();
        Assert.Contains("./Config.json:/ServiceBus_Emulator/ConfigFiles/Config.json:ro",
            spec.ComposeYaml);
    }

    [Fact]
    public void ServiceBus_Without_Config_Json_Has_No_Volume_Mount_For_Config()
    {
        var spec = AdjacentSidecars.ProvisionAll().WithServiceBusEmulator().Build();
        // Sanity check: no `Config.json:ro` substring when ConfigJsonPath isn't set.
        Assert.DoesNotContain("Config.json:ro", spec.ComposeYaml);
    }

    [Fact]
    public void ServiceBus_Custom_Port_Propagates_To_Connection_String()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithServiceBusEmulator(s => s.WithHostPort(15672))
            .Build();
        Assert.Contains("\"15672:5672\"", spec.ComposeYaml);
        Assert.Contains("Endpoint=sb://localhost:15672/", spec.ExportedEnvVars["TAMP_SBUS_CONNECTION"]);
    }

    // ─── All-three combo ─────────────────────────────────────────────────

    [Fact]
    public void All_Three_Sidecars_Combined()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithProjectName("multi-sidecar-test")
            .WithPostgres(p => p.WithDatabase("test"))
            .WithAzurite()
            .WithServiceBusEmulator()
            .Build();

        Assert.Equal(3, spec.ExportedEnvVars.Count);
        Assert.True(spec.ExportedEnvVars.ContainsKey("TAMP_PG_CONNECTION"));
        Assert.True(spec.ExportedEnvVars.ContainsKey("TAMP_AZURITE_CONNECTION"));
        Assert.True(spec.ExportedEnvVars.ContainsKey("TAMP_SBUS_CONNECTION"));
        // Volumes section includes both data volumes (sb doesn't have one).
        Assert.Contains("postgres-data: {}", spec.ComposeYaml);
        Assert.Contains("azurite-data: {}", spec.ComposeYaml);
    }

    [Fact]
    public void Build_Is_Deterministic_Idempotent()
    {
        var b = AdjacentSidecars.ProvisionAll()
            .WithProjectName("repro")
            .WithPostgres(p => p.WithDatabase("d"))
            .WithAzurite();
        var s1 = b.Build();
        var s2 = b.Build();
        Assert.Equal(s1.ComposeYaml, s2.ComposeYaml);
        Assert.Equal(s1.ExportedEnvVars.OrderBy(kvp => kvp.Key).ToList(),
                     s2.ExportedEnvVars.OrderBy(kvp => kvp.Key).ToList());
    }

    [Fact]
    public void Service_Name_Override_Reflected_In_Yaml()
    {
        var spec = AdjacentSidecars.ProvisionAll()
            .WithPostgres(p => p.WithServiceName("pg-tenant-1"))
            .Build();
        Assert.Contains("  pg-tenant-1:", spec.ComposeYaml);
        Assert.Contains("pg-tenant-1-data:", spec.ComposeYaml);
        // Default service name should NOT appear.
        Assert.DoesNotContain("  postgres:\n", spec.ComposeYaml);
    }

    // ─── ApplyToProcessEnvironment ────────────────────────────────────────

    [Fact]
    public void ApplyToProcessEnvironment_Sets_The_Vars()
    {
        // Use a unique project name so we don't clobber another test's env vars on a parallel run.
        var pg = "Host=localhost;Port=54398;Database=apply_test;Username=u;Password=p";
        try
        {
            var spec = AdjacentSidecars.ProvisionAll()
                .WithPostgres(p => p.WithHostPort(54398).WithDatabase("apply_test").WithUsername("u").WithPassword("p"))
                .Build();
            spec.ApplyToProcessEnvironment();
            Assert.Equal(pg, Environment.GetEnvironmentVariable("TAMP_PG_CONNECTION"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TAMP_PG_CONNECTION", null);
        }
    }
}
