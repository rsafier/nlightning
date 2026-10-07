using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Walletrpc;

namespace NLightning.Integration.Tests.Docker.Utils;

using Domain.Persistence.Interfaces;
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
            var tipHash = await fixture.Bitcoin.GetBlockHashAsync((int)tip, cancellationToken);
            var allAtTip = true;
            status.Clear().Append("tip ").Append(tip).Append(' ').Append(tipHash);

            foreach (var lnd in lndList)
            {
                string state;
                try
                {
                    var info = await lnd.LightningClient.GetInfoAsync(new GetInfoRequest(),
                                                                      cancellationToken: cancellationToken);
                    allAtTip &= info.SyncedToChain && info.BlockHeight == tip
                             && string.Equals(info.BlockHash, tipHash.ToString(), StringComparison.OrdinalIgnoreCase);
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
                // NL-1228: a same-height replacement branch is not processed until its hash is committed.
                using var scope = node.Services.CreateScope();
                var committed = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                                           .BlockchainStateDbRepository.GetStateAsync();
                allAtTip &= height == tip && committed?.LastProcessedHeight == tip
                         && new uint256((byte[])committed.LastProcessedBlockHash) == tipHash;
                status.Append(", ").Append(node.Name).Append(' ').Append(height)
                      .Append(' ').Append(committed?.LastProcessedBlockHash);
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

    /// <summary>
    /// Makes sure <paramref name="lnd"/> can fund <paramref name="minSat"/> from confirmed wallet outputs that no lease
    /// holds, keeping its anchors reserve: LND's sweeper leases wallet outputs as fee inputs of its anchor and HTLC
    /// sweeps (after an earlier test's force close), and alice's open right after found "not enough witness outputs to
    /// create funding transaction, need 0.01000000 BTC only have 0 BTC available" (NL-842). When short, the miner sends
    /// two outputs of 0.1 BTC (the next sweep may lease one), mines a block and waits until LND counts them.
    /// </summary>
    /// <exception cref="TimeoutException">LND did not count the coins in time.</exception>
    public static async Task EnsureLndSpendableAsync(LightningRegtestNetworkFixture fixture, LndNodeConnection lnd,
                                                     long minSat, IEnumerable<NLightningTestNode> nodes,
                                                     CancellationToken cancellationToken)
    {
        var nodeList = nodes.ToList();
        var spendable = await SpendableSatAsync(lnd, cancellationToken);
        if (spendable >= minSat)
            return;

        Console.WriteLine($"{lnd.LocalAlias} can spend {spendable} sat, needs {minSat}: funding it");
        for (var i = 0; i < 2; i++)
        {
            var address = await lnd.LightningClient.NewAddressAsync(
                              new NewAddressRequest
                              {
                                  Type = NLightning.Testing.Lnd.Lnrpc.AddressType.WitnessPubkeyHash
                              }, cancellationToken: cancellationToken);
            await fixture.Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Address, Network.RegTest),
                                                     Money.Coins(0.1m), cancellationToken: cancellationToken);
        }

        await MineAndWaitAsync(fixture, 1, fixture.LndNodes, nodeList, cancellationToken);
        var deadline = DateTime.UtcNow + DefaultTimeout;
        while ((spendable = await SpendableSatAsync(lnd, cancellationToken)) < minSat)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{lnd.LocalAlias} can spend {spendable} sat after its funding, needs {minSat}");

            await Task.Delay(s_pollInterval, cancellationToken);
        }
    }

    /// <summary>Confirmed wallet balance, less leased outputs and the anchors reserve.</summary>
    private static async Task<long> SpendableSatAsync(LndNodeConnection lnd, CancellationToken cancellationToken)
    {
        var balance = await lnd.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(),
                                                                   cancellationToken: cancellationToken);
        return balance.ConfirmedBalance - balance.LockedBalance - balance.ReservedBalanceAnchorChan;
    }
}