using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Tamp.AdjacentContainer.Provisioning;

/// <summary>
/// Resolves a short, stable per-worker discriminator (and a derived port offset) so N agents in N
/// worktrees can provision the same sidecar set concurrently without compose-project-name or host-port
/// collisions (tamp-build/tamp#18, ADR 0019 Pillar 4 — correct-by-default).
///
/// Resolution: explicit <c>TAMP_WORKER_ID</c> (sanitized) → short hash of the worktree root path.
/// </summary>
internal static class WorkerScope
{
    /// <summary>A safe discriminator token (e.g. <c>w-a1b2c3d4e5</c>). Inputs injectable for deterministic tests.</summary>
    public static string Discriminator(Func<string, string?>? getEnv = null, string? worktree = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;

        var explicitId = getEnv("TAMP_WORKER_ID");
        if (!string.IsNullOrWhiteSpace(explicitId))
            return Sanitize(explicitId!);

        worktree ??= SafeWorktree();
        return "w-" + ShortHash(worktree);
    }

    /// <summary>
    /// A deterministic host-port offset in <c>[1, range]</c> derived from the discriminator, so a
    /// worker's sidecars land on a distinct port band while its own connection strings stay consistent.
    /// </summary>
    public static int PortOffset(string discriminator, int range = 4000)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(discriminator ?? string.Empty));
        var n = BitConverter.ToUInt32(bytes, 0);
        return (int)(n % (uint)range) + 1;
    }

    private static string Sanitize(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        var token = sb.ToString().Trim('-');
        while (token.Contains("--")) token = token.Replace("--", "-");
        if (token.Length > 40) token = token[..40].Trim('-');
        return token.Length == 0 ? "w-unknown" : token;
    }

    private static string ShortHash(string value)
    {
        var normalized = (value ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..10];
    }

    private static string SafeWorktree()
    {
        try { return TampBuild.RootDirectory.Value; }
        catch { }
        try { return Directory.GetCurrentDirectory(); }
        catch { return "unknown"; }
    }
}
