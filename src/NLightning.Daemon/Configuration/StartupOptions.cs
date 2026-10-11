using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Configuration;

using Contracts.Constants;

/// <summary>
/// <c>Node:Startup</c>: the locked start (NL-1349, <c>docs/agents/LOCKED_START.md</c>). With <see cref="Locked"/>
/// (or <c>--locked</c>) the daemon starts without any key material and opens only a provisioning endpoint; the node
/// starts once a key provisioner delivered a key that matches the database's signing enrollment. Read by hand (like
/// <see cref="SigningOptions"/>), so the configuration binder's rules do not apply.
/// </summary>
public sealed class StartupOptions
{
    public const string SectionName = "Node:Startup";
    public const string SocketProvisioner = "Socket";
    public const string StdinProvisioner = "Stdin";

    /// <summary>A Unix socket path must fit <c>sockaddr_un.sun_path</c> (104 bytes on macOS, 108 on Linux).</summary>
    public const int MaxSocketPathBytes = 103;

    public bool Locked { get; set; }

    /// <summary><see cref="SocketProvisioner"/> (default) or <see cref="StdinProvisioner"/>.</summary>
    public string Provisioner { get; set; } = SocketProvisioner;

    /// <summary>The provisioning socket; default <c>&lt;configPath&gt;/provisioning/key.sock</c>.</summary>
    public string? SocketPath { get; set; }

    /// <summary>The wait after a refused key before the next attempt is read (default 1 s, 0..60 s).</summary>
    public int FailureDelayMilliseconds { get; set; } = 1000;

    public bool IsStdin => string.Equals(Provisioner, StdinProvisioner, StringComparison.OrdinalIgnoreCase);

    public static StartupOptions Read(IConfiguration configuration, bool lockedFlag)
    {
        var section = configuration.GetSection(SectionName);
        return new StartupOptions
        {
            Locked = lockedFlag || section.GetValue("Locked", false),
            Provisioner = section["Provisioner"] is { Length: > 0 } provisioner ? provisioner : SocketProvisioner,
            SocketPath = section["SocketPath"] is { Length: > 0 } socketPath ? socketPath : null,
            FailureDelayMilliseconds = section.GetValue("FailureDelayMilliseconds", 1000)
        };
    }

    public string GetSocketPath(string configPath) =>
        SocketPath ?? Path.Combine(configPath, NodeConstants.ProvisioningSocketDirectory,
                                   NodeConstants.ProvisioningSocketFile);

    public IReadOnlyList<string> GetValidationErrors(string configPath)
    {
        var errors = new List<string>();
        if (!string.Equals(Provisioner, SocketProvisioner, StringComparison.OrdinalIgnoreCase) && !IsStdin)
            errors.Add("Node:Startup:Provisioner must be Socket or Stdin.");
        if (SocketPath is not null && !Path.IsPathFullyQualified(SocketPath))
            errors.Add("Node:Startup:SocketPath must be an absolute path.");
        if (Encoding.UTF8.GetByteCount(Path.GetFullPath(GetSocketPath(configPath))) > MaxSocketPathBytes)
            errors.Add($"The provisioning socket path is longer than {MaxSocketPathBytes} bytes; set a shorter "
                     + "Node:Startup:SocketPath.");
        if (FailureDelayMilliseconds is < 0 or > 60_000)
            errors.Add("Node:Startup:FailureDelayMilliseconds must be between 0 and 60000.");
        return errors;
    }
}