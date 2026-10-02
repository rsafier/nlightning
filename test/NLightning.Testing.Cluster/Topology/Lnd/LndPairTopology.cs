using System.Diagnostics;

namespace NLightning.Testing.Cluster.Topology.Lnd;

using Nodes;
using Nodes.BitcoinCore;
using Nodes.Lnd;
using Run;

/// <summary>
/// The spike's LND topology (plan §5): bitcoind <c>miner</c> and two LND nodes, <c>alice</c> and <c>bob</c>, with a
/// public channel from alice to bob that both report active. Built into a <see cref="TestRun"/>'s namespace; the run's
/// disposal tears it down.
/// </summary>
public sealed class LndPairTopology : IDisposable
{
    private LndPairTopology(BitcoinCoreTopologyChain chain, LndNode alice, LndNode bob, TestChannel channel,
                            IReadOnlyDictionary<string, TimeSpan> timings)
    {
        Chain = chain;
        Alice = alice;
        Bob = bob;
        Channel = channel;
        Timings = timings;
    }

    /// <summary>The bitcoind <c>miner</c> and its chain helpers (<see cref="BitcoinCoreTopologyChain.Chain"/>).</summary>
    public BitcoinCoreTopologyChain Chain { get; }

    public LndNode Alice { get; }

    public LndNode Bob { get; }

    /// <summary>The alice → bob channel as alice sees it once active.</summary>
    public TestChannel Channel { get; }

    /// <summary>How long each phase took (bitcoind, LND nodes, funding, channel), for the spike's timing comparison.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings { get; }

    /// <summary>The topology's settings.</summary>
    public sealed record Settings
    {
        public long CapacitySat { get; init; } = 1_000_000;

        public long PushMsat { get; init; }

        public bool Announce { get; init; } = true;

        /// <summary>Sent to alice's wallet before the open.</summary>
        public long AliceFundingSat { get; init; } = 100_000_000;

        public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);

        /// <summary>Extra LND flags for both nodes.</summary>
        public IReadOnlyList<string> LndExtraArgs { get; init; } = [];

        public Action<string>? Log { get; init; }
    }

    /// <summary>
    /// Deploys bitcoind, mines past coinbase maturity (LND reports <c>synced_to_chain</c> only after a recent block),
    /// deploys alice and bob in parallel, funds alice, connects, opens the channel, mines 6 blocks and waits until both
    /// sides report it active.
    /// </summary>
    public static async Task<LndPairTopology> BuildAsync(TestRun run, Settings settings,
                                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(settings);

        var timings = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var watch = Stopwatch.StartNew();
        void Phase(string name)
        {
            timings[name] = watch.Elapsed;
            settings.Log?.Invoke($"[lnd-pair] {run.Namespace}: {name} after {watch.Elapsed.TotalSeconds:F1} s");
            watch.Restart();
        }

        // Mined past coinbase maturity at deploy
        var chain = await BitcoinCoreTopologyChain.DeployAsync(run, new BitcoinCoreOptions(), settings.ReadyTimeout,
                                                               cancellationToken)
                                                  .ConfigureAwait(false);
        Phase("bitcoind");

        LndNodeOptions Lnd(string alias) =>
            LndNodeDeployer.BuildOptions(chain, new TopologyNodeSpec(alias, NodeKind.Lnd, null, settings.LndExtraArgs));
        var aliceTask = LndNode.DeployAsync(run, Lnd("alice"), settings.ReadyTimeout, cancellationToken);
        var bobTask = LndNode.DeployAsync(run, Lnd("bob"), settings.ReadyTimeout, cancellationToken);
        try
        {
            await Task.WhenAll(aliceTask, bobTask).ConfigureAwait(false);
            var alice = aliceTask.Result;
            var bob = bobTask.Result;
            Phase("lnd");

            var address = await alice.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
            await chain.SendToAddressAsync(address, settings.AliceFundingSat, cancellationToken).ConfigureAwait(false);
            await chain.MineAsync(1, cancellationToken).ConfigureAwait(false);
            var height = await chain.GetBlockCountAsync(cancellationToken).ConfigureAwait(false);
            await alice.WaitSyncedToChainAsync(settings.ReadyTimeout, cancellationToken, (uint)height)
                       .ConfigureAwait(false);
            await WaitForBalanceAsync(alice, settings.CapacitySat, settings.ReadyTimeout, cancellationToken)
                .ConfigureAwait(false);
            Phase("funding");

            var bobAddress = await bob.GetAddressAsync(cancellationToken).ConfigureAwait(false);
            await alice.ConnectAsync(bobAddress, cancellationToken).ConfigureAwait(false);
            var open = await alice.OpenChannelAsync(new TestOpenChannelRequest(bobAddress.NodeId,
                                                                               settings.CapacitySat,
                                                                               settings.PushMsat,
                                                                               settings.Announce),
                                                    cancellationToken).ConfigureAwait(false);
            await chain.Chain.WaitForMempoolAsync(open.FundingTxId, cancellationToken, settings.ReadyTimeout)
                       .ConfigureAwait(false);
            await chain.MineAsync(6, cancellationToken).ConfigureAwait(false);
            var channel = await alice.WaitForActiveChannelAsync(open.FundingTxId, settings.ReadyTimeout,
                                                                cancellationToken).ConfigureAwait(false);
            await bob.WaitForActiveChannelAsync(open.FundingTxId, settings.ReadyTimeout, cancellationToken)
                     .ConfigureAwait(false);
            Phase("channel");

            return new LndPairTopology(chain, alice, bob, channel, timings);
        }
        catch
        {
            DisposeCompleted(aliceTask);
            DisposeCompleted(bobTask);
            throw;
        }
    }

    /// <summary>
    /// Pays <paramref name="amountMsat"/> from <paramref name="payer"/> to <paramref name="payee"/>, retrying a failed
    /// attempt until <paramref name="timeout"/>: LND lists a fresh channel active before its router has the edge
    /// (NL-319).
    /// </summary>
    public static async Task<(TestInvoice Invoice, TestPaymentResult Result, int Attempts)> PayAsync(
        ILightningTestPeer payer, ILightningTestPeer payee, long amountMsat, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payer);
        ArgumentNullException.ThrowIfNull(payee);

        var deadline = DateTime.UtcNow + timeout;
        var invoice = await payee.CreateInvoiceAsync(amountMsat, $"harness spike {payer.Alias} to {payee.Alias}",
                                                     cancellationToken).ConfigureAwait(false);
        var attempts = 0;
        while (true)
        {
            attempts++;
            var result = await payer.PayInvoiceAsync(invoice.Bolt11, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded || DateTime.UtcNow >= deadline)
                return (invoice, result, attempts);

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restarts (or, with <paramref name="kill"/>, crashes) <paramref name="node"/> on its PVC, has the other node dial
    /// it again and waits until both report the channel active. The redial is needed: LND keeps the <em>resolved IP</em>
    /// of a peer it dialled by name, the restarted pod has a new IP (alias, pod name and data stay; the IP does not), and
    /// without announced addresses neither side finds the other again by itself. The redial goes to the new pod IP, not
    /// the alias: right after a restart the cluster DNS can still answer with the old IP for a while (its cache).
    /// </summary>
    public async Task RestartAsync(LndNode node, bool kill, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);
        var other = ReferenceEquals(node, Alice) ? Bob
                  : ReferenceEquals(node, Bob) ? Alice
                  : throw new ArgumentException($"{node.Alias} is not a node of this topology", nameof(node));

        if (kill)
            await node.KillAsync(timeout, cancellationToken).ConfigureAwait(false);
        else
            await node.RestartAsync(timeout, cancellationToken).ConfigureAwait(false);

        var nodeId = await node.GetNodeIdAsync(cancellationToken).ConfigureAwait(false);
        var podIp = node.Handle.PodIp ?? throw new InvalidOperationException($"{node.Alias} has no pod IP");
        await other.ConnectAsync(new TestPeerAddress(nodeId, podIp, LndWorkload.P2pPort), cancellationToken)
                   .ConfigureAwait(false);
        await node.WaitForActiveChannelAsync(Channel.FundingTxId, timeout, cancellationToken).ConfigureAwait(false);
        await other.WaitForActiveChannelAsync(Channel.FundingTxId, timeout, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Alice.Dispose();
        Bob.Dispose();
    }

    private static Task WaitForBalanceAsync(LndNode node, long atLeastSat, TimeSpan timeout,
                                            CancellationToken cancellationToken) =>
        Poll.UntilAsync(async ct => await node.GetConfirmedBalanceSatAsync(ct).ConfigureAwait(false) > atLeastSat,
                        timeout, TimeSpan.FromMilliseconds(500),
                        $"{node.Alias}: a confirmed balance above {atLeastSat} sat", cancellationToken);

    private static void DisposeCompleted(Task<LndNode> task)
    {
        if (task.IsCompletedSuccessfully)
            task.Result.Dispose();
    }
}