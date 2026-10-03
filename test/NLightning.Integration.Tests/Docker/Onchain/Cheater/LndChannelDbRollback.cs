using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker.Onchain.Cheater;

using Fixtures;
using Utils;

/// <summary>
/// Makes an LND node cheat (BOLT 5 plan §5 Proof O5 (a)): a copy of its <c>channel.db</c> is taken while the node is
/// stopped, and later put back while it is stopped again, so LND restarts on an old channel state and a force close
/// broadcasts a revoked commitment.
/// </summary>
/// <remarks>
/// <para>The database lives under the data directory of the image (<c>test/Docker/custom_lnd</c>: <c>VOLUME
/// /home/lnd/.lnd</c>, LND runs as the <c>lnd</c> user); the plan's <c>/root/.lnd/...</c> is tried as well and the
/// path found is logged.</para>
/// <para>On the cluster (test harness phase 6; the only backend of the LND network since NL-820) the node's
/// StatefulSet is stopped and a maintenance pod on its PVC copies the database to <see cref="SnapshotSuffix"/> next to
/// it, and later back (<c>LndRegtestNetwork.RestartAsync</c> with a stop window, which also has the network's LND peers
/// redial the new pod IP); the snapshot never leaves the PVC.</para>
/// </remarks>
public sealed class LndChannelDbRollback
{
    public static readonly IReadOnlyList<string> CandidatePaths =
    [
        "/home/lnd/.lnd/data/graph/regtest/channel.db",
        "/root/.lnd/data/graph/regtest/channel.db"
    ];

    /// <summary>The cluster backend's snapshot, next to the database on the node's PVC.</summary>
    public const string SnapshotSuffix = ".nltg-snapshot";

    private static readonly TimeSpan s_syncTimeout = TimeSpan.FromMinutes(2);

    private readonly string _container;
    private readonly LightningRegtestNetworkFixture _fixture;
    private bool _snapshotTaken;

    /// <summary>The database path found on the node's PVC.</summary>
    public string? DatabasePath { get; private set; }

    /// <param name="fixture">The network.</param>
    /// <param name="container">The LND node's alias (its pod and StatefulSet name).</param>
    public LndChannelDbRollback(LightningRegtestNetworkFixture fixture, string container)
    {
        _fixture = fixture;
        _container = container;
    }

    /// <summary>Stops the node, copies its <c>channel.db</c> and starts it again.</summary>
    public async Task TakeSnapshotAsync(CancellationToken ct)
    {
        await _fixture.Cluster.Network.RestartAsync(_container, cancellationToken: ct,
                                                    whileStopped: async (shell, token) =>
        {
            // The first candidate that exists, copied next to itself (owner and mode kept)
            var script = string.Join(" ", CandidatePaths.Select(p =>
                             $"if [ -f '{p}' ]; then cp -p '{p}' '{p}{SnapshotSuffix}' && echo '{p}' && exit 0; fi;"))
                       + " echo 'no channel.db' >&2; exit 1";
            var result = await shell.RunScriptAsync(script, token);
            DatabasePath = result.StdOutText.Trim();
            _snapshotTaken = true;
            Console.WriteLine($"[lnd-rollback] {_container}: copied {DatabasePath} to {DatabasePath}{SnapshotSuffix} "
                            + $"on its PVC ({shell.PodName})");
        });
        await WaitSyncedAsync(ct);
    }

    /// <summary>Stops the node, puts the snapshot back and starts it again.</summary>
    public async Task RestoreSnapshotAsync(CancellationToken ct)
    {
        if (!_snapshotTaken || DatabasePath is null)
            throw new InvalidOperationException("Take a snapshot first");

        var path = DatabasePath;
        await _fixture.Cluster.Network.RestartAsync(_container, cancellationToken: ct,
                                                    whileStopped: async (shell, token) =>
        {
            await shell.RunScriptAsync($"cp -p '{path}{SnapshotSuffix}' '{path}'", token);
            Console.WriteLine($"[lnd-rollback] {_container}: restored {path} on its PVC ({shell.PodName})");
        });
        await WaitSyncedAsync(ct);
    }

    /// <summary>
    /// Waits until the node's LND answers and is synced to the chain (after a restart).
    /// </summary>
    public async Task<LndNodeConnection> WaitSyncedAsync(CancellationToken ct)
    {
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var lnd = _fixture.GetLndNode(_container);
                var info = await lnd.LightningClient.GetInfoAsync(new GetInfoRequest(),
                                                                  deadline: DateTime.UtcNow.AddSeconds(5),
                                                                  cancellationToken: ct);
                return info.SyncedToChain ? lnd : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null;
            }
        }, s_syncTimeout, $"{_container} synced after its restart", ct);
    }
}