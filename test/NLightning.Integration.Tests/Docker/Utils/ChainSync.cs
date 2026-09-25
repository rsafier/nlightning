using System.Text;
using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Utils;

using Fixtures;

/// <summary>
/// The chain-sync barrier of the multi-node Docker tests: after every mine, wait until every LND node reports
/// <c>synced_to_chain</c> at bitcoind's tip and every NLightning node's monitor has processed the tip. Nodes that
/// disagree on the height compute different CLTVs, which LND reports as <c>expiry_too_soon</c> or
/// <c>incorrect_cltv_expiry</c>.
/// </summary>
public static class ChainSync
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Waits until every node is at bitcoind's current tip. The tip is re-read on every poll, so a block mined
    /// meanwhile only moves the target.
    /// </summary>
    /// <returns>The tip everybody reached.</returns>
    /// <exception cref="TimeoutException">Some node did not reach the tip in time; the message lists every height.</exception>
    public static async Task<uint> WaitAllAtTipAsync(LightningRegtestNetworkFixture fixture,
                                                     IEnumerable<LNDNodeConnection> lndNodes,
                                                     IEnumerable<NLightningTestNode> nodes,
                                                     CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var lndList = lndNodes.ToList();
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var status = new StringBuilder();

        while (true)
        {
            var tip = (uint)await fixture.Bitcoin.GetBlockCountAsync(cancellationToken);
            var allAtTip = true;
            status.Clear().Append("tip ").Append(tip);

            foreach (var lnd in lndList)
            {
                string state;
                try
                {
                    var info = await lnd.LightningClient.GetInfoAsync(new GetInfoRequest(),
                                                                      cancellationToken: cancellationToken);
                    allAtTip &= info.SyncedToChain && info.BlockHeight == tip;
                    state = $"{info.BlockHeight}{(info.SyncedToChain ? string.Empty : " (not synced)")}";
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A restarting LND node refuses RPCs for a moment
                    allAtTip = false;
                    state = $"unreachable: {e.Message}";
                }

                status.Append(", ").Append(lnd.LocalAlias).Append(' ').Append(state);
            }

            foreach (var node in nodeList)
            {
                if (!node.IsRunning)
                {
                    allAtTip = false;
                    status.Append(", ").Append(node.Name).Append(" stopped");
                    continue;
                }

                var height = node.BlockchainMonitor.LastProcessedBlockHeight;
                allAtTip &= height == tip;
                status.Append(", ").Append(node.Name).Append(' ').Append(height);
            }

            if (allAtTip)
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Nodes not at the chain tip in time: {status}");

            await Task.Delay(s_pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until every LND node of the fixture and every node in <paramref name="nodes"/> is at the tip.
    /// </summary>
    public static Task<uint> WaitAllAtTipAsync(LightningRegtestNetworkFixture fixture,
                                               IEnumerable<NLightningTestNode> nodes,
                                               CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        WaitAllAtTipAsync(fixture, fixture.LndNodes, nodes, cancellationToken, timeout);

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks to a bitcoind address, then waits at the barrier.
    /// </summary>
    /// <returns>The new tip.</returns>
    public static async Task<uint> MineAndWaitAsync(LightningRegtestNetworkFixture fixture, int blocks,
                                                    IEnumerable<LNDNodeConnection> lndNodes,
                                                    IEnumerable<NLightningTestNode> nodes,
                                                    CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var bitcoin = fixture.Bitcoin;
        await bitcoin.GenerateToAddressAsync(blocks, await bitcoin.GetNewAddressAsync(cancellationToken),
                                             cancellationToken);
        return await WaitAllAtTipAsync(fixture, lndNodes, nodes, cancellationToken, timeout);
    }
}