using System.Text;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Walletrpc;

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
                                                     IEnumerable<LndNodeConnection> lndNodes,
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
                                                    IEnumerable<LndNodeConnection> lndNodes,
                                                    IEnumerable<NLightningTestNode> nodes,
                                                    CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var bitcoin = fixture.Bitcoin;
        await bitcoin.GenerateToAddressAsync(blocks, await bitcoin.GetNewAddressAsync(cancellationToken),
                                             cancellationToken);
        return await WaitAllAtTipAsync(fixture, lndNodes, nodes, cancellationToken, timeout);
    }

    /// <summary>
    /// Mines one block at a time, <paramref name="pace"/> apart (default 1 s), until <paramref name="lnd"/> lists a
    /// confirmed sweep of an output of <paramref name="closingTxId"/> (its <c>to_remote</c> after our force close).
    /// </summary>
    /// <remarks>
    /// LND 0.21.4's sweeper gives up a commit sweep for good when the sweep confirms in a block its fee bumper has not
    /// processed yet ("Fail to fee bump tx ...: input no longer exists", then "unable to progress
    /// *contractcourt.commitSweepResolver"): the channel then stays pending force close forever with the swept amount
    /// in limbo, and <c>ClosedChannels</c> never lists it (NL-770). A burst of blocks right after the close (LND still
    /// catching up on its 3 close confirmations) triggers it, so a test that later asserts LND's
    /// <c>RemoteForceClose</c> lets LND sweep at this pace first.
    /// </remarks>
    public static async Task MineUntilLndSweptAsync(LightningRegtestNetworkFixture fixture, LndNodeConnection lnd,
                                                    IEnumerable<NLightningTestNode> nodes, uint256 closingTxId,
                                                    CancellationToken cancellationToken, TimeSpan? pace = null,
                                                    int maxBlocks = 30)
    {
        var nodeList = nodes.ToList();
        var prefix = closingTxId + ":";
        for (var i = 0; i <= maxBlocks; i++)
        {
            var sweeps = await lnd.WalletKitClient.ListSweepsAsync(new ListSweepsRequest { Verbose = true },
                                                                   cancellationToken: cancellationToken);
            if (sweeps.TransactionDetails.Transactions.Any(
                    t => t.NumConfirmations > 0
                      && t.PreviousOutpoints.Any(o => o.Outpoint.StartsWith(prefix, StringComparison.Ordinal))))
                return;

            if (i == maxBlocks)
                break;

            await MineAndWaitAsync(fixture, 1, [lnd], nodeList, cancellationToken);
            await Task.Delay(pace ?? TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new TimeoutException($"{lnd.LocalAlias} swept no output of {closingTxId} within {maxBlocks} blocks");
    }
}