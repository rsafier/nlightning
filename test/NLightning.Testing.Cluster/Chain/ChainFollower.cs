namespace NLightning.Testing.Cluster.Chain;

using Nodes.BitcoinCore.Rpc;

/// <summary>What a node reports about its own chain view: height, optionally the tip hash, and whether it says it is synced.</summary>
/// <param name="Height">The height the node has processed.</param>
/// <param name="Hash">Its tip hash, or null when the node does not report one (then only the height is compared).</param>
/// <param name="Synced">The node's own "synced" flag (LND <c>synced_to_chain</c>); true when it has none.</param>
public sealed record FollowerReport(long Height, string? Hash = null, bool Synced = true);

/// <summary>A follower's state at one poll: whether it is at the tip, and a short text for timeout messages.</summary>
public sealed record FollowerStatus(bool AtTip, string State);

/// <summary>
/// Something that follows the chain (a Lightning node, another bitcoind, the in-process NLightning node's monitor) and
/// can say whether it has processed a given tip (plan R10: "mine and wait until every node in the topology has processed
/// the tip"). The predicate is per follower, so each implementation plugs its own check in.
/// </summary>
public sealed class ChainFollower
{
    private readonly Func<ChainTip, CancellationToken, Task<FollowerStatus>> _probe;

    /// <param name="name">The follower's name in messages (its alias).</param>
    /// <param name="probe">Whether it is at the given tip; an exception counts as "not yet" (a restarting node).</param>
    public ChainFollower(string name, Func<ChainTip, CancellationToken, Task<FollowerStatus>> probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public string Name { get; }

    /// <summary>
    /// A follower that reports a <see cref="FollowerReport"/>: at the tip when it is synced, at the tip's height and,
    /// when it reports a hash, at the tip's hash.
    /// </summary>
    public static ChainFollower FromReport(string name, Func<CancellationToken, Task<FollowerReport>> report) =>
        new(name, async (tip, ct) => Evaluate(tip, await report(ct).ConfigureAwait(false)));

    /// <summary>A follower that reports only its height.</summary>
    public static ChainFollower AtHeight(string name, Func<CancellationToken, Task<long>> height) =>
        FromReport(name, async ct => new FollowerReport(await height(ct).ConfigureAwait(false)));

    /// <summary>Another bitcoind (a peer of the miner): at the tip when its best block is the tip.</summary>
    public static ChainFollower Bitcoind(string name, IBitcoinCoreRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        return FromReport(name, async ct =>
        {
            var tip = await rpc.GetTipAsync(ct).ConfigureAwait(false);
            return new FollowerReport(tip.Height, tip.Hash);
        });
    }

    /// <summary>The follower's state at <paramref name="tip"/>; an exception is "not at the tip" with its message.</summary>
    public async Task<FollowerStatus> ProbeAsync(ChainTip tip, CancellationToken cancellationToken)
    {
        try
        {
            return await _probe(tip, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new FollowerStatus(false, $"unreachable: {e.Message}");
        }
    }

    /// <summary>The state of a <see cref="FollowerReport"/> at <paramref name="tip"/>.</summary>
    public static FollowerStatus Evaluate(ChainTip tip, FollowerReport report)
    {
        ArgumentNullException.ThrowIfNull(tip);
        ArgumentNullException.ThrowIfNull(report);
        var hashMatches = report.Hash is null || string.Equals(report.Hash, tip.Hash, StringComparison.OrdinalIgnoreCase);
        var atTip = report.Synced && report.Height == tip.Height && hashMatches;
        var state = report.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (report.Height == tip.Height && !hashMatches)
            state += $" on another branch ({report.Hash})";
        if (!report.Synced)
            state += " (not synced)";
        return new FollowerStatus(atTip, state);
    }

    public override string ToString() => Name;
}