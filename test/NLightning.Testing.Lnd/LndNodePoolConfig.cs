// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDNodePoolConfig.cs and LNDNodePoolConfigBuilder.cs),
// Copyright (c) 2024-2025 nbd, MIT License; the full text is in LICENSE-LNUnit.txt next to this file. Changed: the
// readiness check and the connection factory can be replaced, QuickStartupMode is settable, background polling can be
// turned off.

using System.ComponentModel.DataAnnotations;

namespace NLightning.Testing.Lnd;

/// <summary>What an <see cref="LndNodePool"/> holds and how it decides a node is ready.</summary>
public class LndNodePoolConfig
{
    /// <summary>Nodes to connect to; a node that cannot be reached yet is retried on every readiness pass.</summary>
    public List<LndSettings> ConnectTo { get; } = [];

    /// <summary>Connections that already exist. The pool owns them from then on and disposes them with itself.</summary>
    public List<LndNodeConnection> Nodes { get; } = [];

    /// <summary>How often, in seconds, the background loop re-checks every node.</summary>
    [Range(1, 60, ErrorMessage = "Value for {0} must be between {1} and {2}.")]
    public int UpdateReadyStatesPeriod { get; set; } = 5;

    /// <summary>
    /// Poll every 100 ms until every node is ready or 10 s passed, then every <see cref="UpdateReadyStatesPeriod"/>.
    /// </summary>
    public bool QuickStartupMode { get; set; } = true;

    /// <summary>Run the readiness loop in the background (LNUnit.LND always did). Off: call <see cref="LndNodePool.UpdateReadyStatesAsync"/>.</summary>
    public bool StartBackgroundUpdates { get; set; } = true;

    /// <summary>
    /// Whether a node is ready; by default <c>State.GetState</c> answers <c>SERVER_ACTIVE</c> within 2 s. A harness
    /// can also require, e.g., <c>synced_to_chain</c>.
    /// </summary>
    public Func<LndNodeConnection, CancellationToken, Task<bool>>? ReadinessCheck { get; set; }

    /// <summary>Opens a connection for <see cref="ConnectTo"/> entries; by default <see cref="LndNodeConnection.ConnectAsync"/>.</summary>
    public Func<LndSettings, CancellationToken, Task<LndNodeConnection>>? ConnectionFactory { get; set; }
}

/// <summary>LNUnit.LND's fluent helpers for <see cref="LndNodePoolConfig"/>.</summary>
public static class LndNodePoolConfigBuilder
{
    /// <summary>Adds an existing connection.</summary>
    public static LndNodePoolConfig AddNode(this LndNodePoolConfig config, LndNodeConnection connection)
    {
        config.Nodes.Add(connection);
        return config;
    }

    /// <summary>Adds a node to connect to.</summary>
    public static LndNodePoolConfig AddConnectionSettings(this LndNodePoolConfig config, LndSettings settings)
    {
        config.ConnectTo.Add(settings);
        return config;
    }

    /// <summary>Sets <see cref="LndNodePoolConfig.UpdateReadyStatesPeriod"/>.</summary>
    public static LndNodePoolConfig UpdateReadyStatesPeriod(this LndNodePoolConfig config, int period)
    {
        config.UpdateReadyStatesPeriod = period;
        return config;
    }
}