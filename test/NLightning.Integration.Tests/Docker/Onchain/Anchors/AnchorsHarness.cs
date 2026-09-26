using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Application.Channels.Safety.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Constants;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Infrastructure.Bitcoin.Outputs;
using Utils;

/// <summary>
/// What the BOLT 5 O7 anchors proofs (plan O7-T4: anchors variants of Proofs O3-O5 plus the CPFP proof) share: a node
/// that negotiates <c>option_anchors</c> (bits 22/23, <c>option_anchors_zero_fee_htlc_tx</c>; experimental, so only
/// these tests turn it on), anchors channels opened to LND and checked on both ends, our force close through
/// <see cref="IChannelFailureService"/>, and the chain-side facts of anchors transactions (the two 330-sat anchors, the
/// CSV-1 <c>to_remote</c>, <c>SIGHASH_SINGLE|ANYONECANPAY</c> peer signatures, fee inputs).
/// </summary>
/// <remarks>
/// <para>The proofs read only what is observable on chain, in bitcoind's mempool, in LND and in the rows the
/// on-chain executor persists (<c>ChannelCloses</c>, <c>OutputResolutions</c>), never the O7 services themselves
/// (<c>IFeeInputSelector</c>, <c>IAnchorCpfpService</c>, the resolvers' anchors paths), so they hold whatever shape
/// those take. The CPFP child is found as the mempool transaction that spends our anchor.</para>
/// <para>LND 0.20 negotiates anchors by default. An LND node keeps an on-chain reserve for the fee bumping of its
/// anchors channels, so <see cref="EnsureLndWalletFundedAsync"/> gives the peer coins before an open.</para>
/// </remarks>
internal sealed class AnchorsHarness
{
    /// <summary>BOLT 3: every anchor output is 330 sat.</summary>
    public const long AnchorAmountSat = 330;

    /// <summary>BOLT 3: the peer's signature of an anchors HTLC transaction is <c>SIGHASH_SINGLE|ANYONECANPAY</c>.</summary>
    public const byte SigHashSingleAnyoneCanPay = 0x83;

    public const byte SigHashAll = 0x01;

    /// <summary>D9 <c>Onchain:ReasonableDepth</c> default: an HTLC is failed upstream when its claim is this deep.</summary>
    public const uint ReasonableDepth = OutputResolutionFacts.DefaultReasonableDepth;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    public static readonly LightningMoney Capacity = LightningMoney.Satoshis(1_000_000);

    /// <summary>The fixed fee answer of <see cref="NLightningTestNode"/> (10 sat/vB) in sat/kw.</summary>
    public static readonly LightningMoney EstimateFeeRatePerKw = LightningMoney.Satoshis(2_500);

    /// <summary>
    /// The lowest feerate our opener accepts (<see cref="ChannelConstants.MinFeePerKw"/>, 1,000 sat/kw, about 4 sat/vB;
    /// BOLT 3's floor is 253 sat/kw): a commitment well below the 10 sat/vB estimate, which needs a child.
    /// </summary>
    public static readonly LightningMoney LowFeeRatePerKw = ChannelConstants.MinFeePerKw;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public AnchorsHarness(LightningRegtestNetworkFixture fixture)
    {
        _fixture = fixture;
    }

    public LightningRegtestNetworkFixture Fixture => _fixture;
    public IReadOnlyList<NLightningTestNode> Nodes => _nodes;

    /// <summary>
    /// <c>option_anchors</c> Optional (so both 22/23 are advertised as supported), allowed although experimental.
    /// </summary>
    public static void EnableAnchors(NodeOptions options)
    {
        options.Features.AllowExperimentalFeatures = true;
        options.Features.OptionAnchors = FeatureSupport.Optional;
    }

    /// <summary>
    /// A started node with anchors enabled; <paramref name="configure"/> runs after <see cref="EnableAnchors"/>, and
    /// <paramref name="beforeStart"/> before the start (extra configuration, services).
    /// </summary>
    public async Task<NLightningTestNode> CreateNodeAsync(string name, CancellationToken ct,
                                                          Action<NodeOptions>? configure = null,
                                                          Action<NLightningTestNode>? beforeStart = null)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, configureNodeOptions: o =>
        {
            EnableAnchors(o);
            configure?.Invoke(o);
        });
        _nodes.Add(node);
        beforeStart?.Invoke(node);
        await node.StartAsync(ct);
        return node;
    }

    public async Task DisposeNodesAsync(IEnumerable<string> containers)
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(containers);
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
        _nodes.Clear();
    }

    #region Opening

    /// <summary>
    /// Gives <paramref name="lnd"/> at least 0.01 BTC confirmed on chain: LND refuses an anchors channel that would
    /// leave its wallet without the reserve it keeps for fee bumping.
    /// </summary>
    public async Task EnsureLndWalletFundedAsync(LNDNodeConnection lnd, CancellationToken ct)
    {
        var balance = await lnd.LightningClient.WalletBalanceAsync(new WalletBalanceRequest(),
                                                                   cancellationToken: ct);
        if (balance.ConfirmedBalance >= 1_000_000)
            return;

        var address = await lnd.LightningClient.NewAddressAsync(new NewAddressRequest
        {
            Type = Lnrpc.AddressType.WitnessPubkeyHash
        }, cancellationToken: ct);
        await _fixture.Bitcoin.SendToAddressAsync(BitcoinAddress.Create(address.Address, Network.RegTest),
                                                  Money.Coins(0.05m), cancellationToken: ct);
        await ChainSync.MineAndWaitAsync(_fixture, 6, [lnd], _nodes.Where(n => n.IsRunning), ct);
        await Poll.UntilAsync(async () => (await lnd.LightningClient.WalletBalanceAsync(
                                               new WalletBalanceRequest(), cancellationToken: ct)).ConfirmedBalance
                                        >= 1_000_000, Timeout, $"{lnd.LocalAlias}'s wallet funded", ct);
    }

    /// <summary>
    /// Funds our wallet (the channel plus the coins the fee inputs come from), opens a channel of
    /// <see cref="Capacity"/> to <paramref name="peer"/> at <paramref name="feeRatePerKw"/>, waits until both ends use
    /// it and checks it is an anchors channel on both ends.
    /// </summary>
    public async Task<OpenChannelClientSubscriptionResponse> OpenAnchorsChannelAsync(
        NLightningTestNode node, LNDNodeConnection peer, LightningMoney? push, CancellationToken ct,
        LightningMoney? feeRatePerKw = null)
    {
        await EnsureLndWalletFundedAsync(peer, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        var peerAddress = await node.ConnectToAsync(peer, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, Capacity)
        {
            PushAmount = push,
            FeeRatePerKw = feeRatePerKw ?? EstimateFeeRatePerKw
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [node], ct);
            return false;
        }, Timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [node], ct);

        // Both ends agree on an anchors channel type (option_static_remotekey + option_anchors)
        var model = GetModel(node, channel.ChannelId);
        Assert.True(model.ChannelParams.OptionAnchorOutputs, "our channel is not an anchors channel");
        Assert.True(model.ChannelParams.ToChannelType().IsFeatureSet(Feature.OptionAnchors, true));
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        Console.WriteLine($"LND {peer.LocalAlias} commitment type {lndChannel.CommitmentType}, "
                        + $"LND version {await LndTestHelpers.GetVersionAsync(peer, ct)}");
        Assert.Equal(CommitmentType.Anchors, lndChannel.CommitmentType);
        return channel;
    }

    public static ChannelModel GetModel(NLightningTestNode node, ChannelId channelId)
    {
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channelId, out var model),
                    $"channel {channelId} not in memory");
        return model!;
    }

    #endregion

    #region Force close

    public sealed record ConfirmedCommitment(TxId TxId, Transaction Transaction, uint Height);

    /// <summary>
    /// Fails the channel through <see cref="IChannelFailureService"/> (the only broadcaster of our commitment) and
    /// waits until the commitment is in bitcoind's mempool.
    /// </summary>
    public async Task<Transaction> ForceCloseAsync(NLightningTestNode node,
                                                   OpenChannelClientSubscriptionResponse channel,
                                                   CancellationToken ct)
    {
        var outcome = await node.Services.GetRequiredService<IChannelFailureService>()
                                .FailChannelAsync(channel.ChannelId,
                                                  new ChannelFailureRequest("O7 anchors proof force close",
                                                                            "force closing the channel"), ct);
        Assert.NotNull(outcome.CommitmentTxId);
        var displayTxId = new uint256((byte[])outcome.CommitmentTxId.Value);
        Console.WriteLine($"Force closed {channel.ChannelId}: {outcome.Status}, commitment {displayTxId}");
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(displayTxId),
                              Timeout, "our commitment in the mempool", ct);
        return await _fixture.Bitcoin.GetRawTransactionAsync(displayTxId, true, ct);
    }

    /// <summary>
    /// <see cref="ForceCloseAsync"/>, one block, and the node's record of the funding spend as our local commitment.
    /// </summary>
    public async Task<ConfirmedCommitment> ForceCloseAndConfirmAsync(NLightningTestNode node, LNDNodeConnection[] peers,
                                                                     OpenChannelClientSubscriptionResponse channel,
                                                                     CancellationToken ct)
    {
        var commitment = await ForceCloseAsync(node, channel, ct);
        var txId = commitment.GetHash();
        var info = await MineUntilConfirmedAsync(node, peers, txId, ct);
        var height = (uint)(await _fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1);
        TxId ours = txId.ToBytes();
        var close = await WaitForCloseAsync(node, channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        Assert.Equal(ours, close.CommitmentTransactionId);
        return new ConfirmedCommitment(ours, commitment, height);
    }

    /// <summary>LND <c>CloseChannel { force = true }</c>; returns the commitment's txid once it is broadcast.</summary>
    public static async Task<uint256> LndForceCloseAsync(LNDNodeConnection lnd,
                                                         OpenChannelClientSubscriptionResponse channel,
                                                         CancellationToken ct)
    {
        var parts = channel.ChannelPoint().Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(Timeout);
        using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
            Force = true
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);

        // LND's txid bytes are in internal order
        var txId = new uint256(pending.Txid.ToByteArray());
        Console.WriteLine($"{lnd.LocalAlias} force-closed {channel.ChannelPoint()} with {txId}");
        return txId;
    }

    #endregion

    #region Anchors transaction facts

    /// <summary>The P2WSH anchor output script of a funding key (BOLT 3 <c>to_local_anchor</c>/<c>to_remote_anchor</c>).</summary>
    public static Script AnchorScriptPubKey(byte[] fundingPubKey) =>
        new ToAnchorOutput(LightningMoney.Satoshis(AnchorAmountSat), new PubKey(fundingPubKey)).ToTxOut()
                                                                                               .ScriptPubKey;

    /// <summary>
    /// The vouts of our and the peer's anchor in a commitment of <paramref name="channel"/> (either side's), found by
    /// script from both funding keys.
    /// </summary>
    public static (uint Ours, uint Peers) FindAnchors(ChannelModel channel, Transaction commitment)
    {
        Assert.NotNull(channel.RemoteKeySet);
        var ours = AnchorScriptPubKey(channel.LocalKeySet.FundingCompactPubKey);
        var peers = AnchorScriptPubKey(channel.RemoteKeySet.FundingCompactPubKey);
        var ourVout = commitment.Outputs.FindIndex(o => o.ScriptPubKey == ours);
        var peerVout = commitment.Outputs.FindIndex(o => o.ScriptPubKey == peers);
        Assert.True(ourVout >= 0, "no anchor of ours in the commitment");
        Assert.True(peerVout >= 0, "no anchor of the peer in the commitment");
        Assert.Equal(AnchorAmountSat, commitment.Outputs[ourVout].Value.Satoshi);
        Assert.Equal(AnchorAmountSat, commitment.Outputs[peerVout].Value.Satoshi);
        return ((uint)ourVout, (uint)peerVout);
    }

    /// <summary>
    /// Whether <paramref name="witness"/> is the BOLT 3 anchors <c>to_remote</c> spend:
    /// <c>&lt;sig&gt; &lt;script&gt;</c> with the script <c>&lt;pubkey&gt; OP_CHECKSIGVERIFY 1 OP_CSV</c>.
    /// </summary>
    public static bool IsAnchorsToRemoteSpend(WitScript witness)
    {
        var script = witness.Pushes.LastOrDefault();
        return script is { Length: 37 } && script[0] == 0x21 && script[34] == 0xad && script[35] == 0x51
            && script[36] == 0xb2;
    }

    /// <summary>The sighash byte of a DER signature push (its last byte).</summary>
    public static byte SigHashOf(byte[] signature) => signature[^1];

    /// <summary>
    /// The mempool transaction that spends <paramref name="outPoint"/>, or null (bitcoind has no mempool index by
    /// outpoint over RPC that every version has, and the regtest mempool is small).
    /// </summary>
    public async Task<Transaction?> FindMempoolSpenderAsync(OutPoint outPoint, CancellationToken ct)
    {
        foreach (var txId in await _fixture.Bitcoin.GetRawMempoolAsync(ct))
        {
            Transaction tx;
            try
            {
                tx = await _fixture.Bitcoin.GetRawTransactionAsync(txId, true, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue; // mined or replaced meanwhile
            }

            if (tx.Inputs.Any(i => i.PrevOut == outPoint))
                return tx;
        }

        return null;
    }

    /// <summary>The sum of the values a transaction's inputs spend (the prevouts looked up with txindex).</summary>
    public async Task<Money> InputValueAsync(Transaction tx, CancellationToken ct)
    {
        var total = Money.Zero;
        foreach (var input in tx.Inputs)
        {
            var parent = await _fixture.Bitcoin.GetRawTransactionAsync(input.PrevOut.Hash, true, ct);
            total += parent.Outputs[(int)input.PrevOut.N].Value;
        }

        return total;
    }

    public async Task<Money> FeeAsync(Transaction tx, CancellationToken ct) =>
        await InputValueAsync(tx, ct) - tx.TotalOut;

    /// <summary>Whether <paramref name="tx"/> spends at least one output not in <paramref name="parents"/>.</summary>
    public static bool HasForeignInput(Transaction tx, params uint256[] parents) =>
        tx.Inputs.Any(i => !parents.Contains(i.PrevOut.Hash));

    #endregion

    #region Chain

    /// <summary>Mines one block at a time until <paramref name="txId"/> is confirmed.</summary>
    public async Task<NBitcoin.RPC.RawTransactionInfo> MineUntilConfirmedAsync(NLightningTestNode node,
                                                                               IEnumerable<LNDNodeConnection> peers,
                                                                               uint256 txId, CancellationToken ct)
    {
        var lnd = peers.ToList();
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(txId, ct);
                if (info.Confirmations >= 1)
                    return info;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not broadcast yet
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, lnd, [node], ct);
            return null;
        }, Timeout, $"transaction {txId} confirmed", ct);
    }

    public Task<NBitcoin.RPC.RawTransactionInfo> MineUntilConfirmedAsync(NLightningTestNode node,
                                                                        IEnumerable<LNDNodeConnection> peers,
                                                                        TxId txId, CancellationToken ct) =>
        MineUntilConfirmedAsync(node, peers, new uint256((byte[])txId), ct);

    /// <summary>
    /// Mines <paramref name="count"/> empty blocks (<c>generateblock</c> with no transactions): the regtest way of a
    /// mempool whose fees the miners do not take.
    /// </summary>
    public async Task MineEmptyBlocksAsync(int count, NLightningTestNode node, LNDNodeConnection[] peers,
                                           CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var address = await _fixture.Bitcoin.GetNewAddressAsync(ct);
            await _fixture.Bitcoin.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        }

        await ChainSync.WaitAllAtTipAsync(_fixture, peers, [node], ct);
    }

    public async Task MineToAsync(NLightningTestNode node, LNDNodeConnection[] peers, uint height,
                                  CancellationToken ct)
    {
        var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        if (height > tip)
            await ChainSync.MineAndWaitAsync(_fixture, (int)(height - tip), peers, [node], ct);
    }

    public async Task<T> MineUntilAsync<T>(NLightningTestNode node, LNDNodeConnection[] peers, Func<Task<T?>> probe,
                                           string what, CancellationToken ct) where T : class
    {
        for (var i = 0; i < 40; i++)
        {
            if (await probe() is { } done)
                return done;

            await ChainSync.MineAndWaitAsync(_fixture, 1, peers, [node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return await Poll.ForAsync(probe, Timeout, what, ct);
    }

    /// <summary>
    /// Mines one block at a time (up to 40) until the node recorded a resolving transaction for the output; returns
    /// it.
    /// </summary>
    public async Task<TxId> MineUntilResolvingTxAsync(NLightningTestNode node, LNDNodeConnection[] peers,
                                                      ChannelId channelId, TxId txId, uint vout, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            var row = (await GetRowsAsync(node, channelId)).FirstOrDefault(r => r.TransactionId == txId
                                                                            && r.OutputIndex == vout);
            if (row?.ResolvingTransactionId is { } resolving)
                return resolving;

            await ChainSync.MineAndWaitAsync(_fixture, 1, peers, [node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return await WaitForResolvingTxAsync(node, channelId, txId, vout, ct);
    }

    public async Task<Transaction> WaitInMempoolAsync(TxId txId, CancellationToken ct)
    {
        var displayTxId = new uint256((byte[])txId);
        await Poll.UntilAsync(async () =>
        {
            if ((await _fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(displayTxId))
                return true;

            // Already mined (the O8 mempool reaction or a fast executor)
            try
            {
                return (await _fixture.Bitcoin.GetRawTransactionInfoAsync(displayTxId, ct)).Confirmations > 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false;
            }
        }, Timeout, $"{displayTxId} in the mempool", ct);
        return await _fixture.Bitcoin.GetRawTransactionAsync(displayTxId, true, ct);
    }

    #endregion

    #region Node state

    public static async Task<ChannelCloseModel> WaitForCloseAsync(NLightningTestNode node, ChannelId channelId,
                                                                  CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            using var scope = node.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                              .GetCloseAsync(channelId);
        }, Timeout, $"the funding spend of {channelId} recorded", ct);

    public static async Task<IReadOnlyList<OutputResolutionModel>> GetRowsAsync(NLightningTestNode node,
                                                                                ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetOutputsByChannelIdAsync(channelId);
    }

    public static async Task<OutputResolutionModel> WaitForRowAsync(NLightningTestNode node, ChannelId channelId,
                                                                    Func<OutputResolutionModel, bool> predicate,
                                                                    string description, CancellationToken ct) =>
        await Poll.ForAsync(async () => (await GetRowsAsync(node, channelId)).FirstOrDefault(predicate), Timeout,
                            description, ct);

    /// <summary>The resolving transaction our node recorded for an output.</summary>
    public static async Task<TxId> WaitForResolvingTxAsync(NLightningTestNode node, ChannelId channelId, TxId txId,
                                                           uint vout, CancellationToken ct)
    {
        var row = await WaitForRowAsync(node, channelId,
                                        r => r.TransactionId == txId && r.OutputIndex == vout
                                                                     && r.ResolvingTransactionId is not null,
                                        $"a resolving transaction for {vout}", ct);
        return row.ResolvingTransactionId!.Value;
    }

    public static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }

    public static IReadOnlyList<HtlcRecord> GetHtlcs(NLightningTestNode node, ChannelId channelId) =>
        node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel) && channel.Commitments is { } c
            ? c.Htlcs.Values.ToList()
            : [];

    public static Task<HtlcRecord> WaitForHtlcInBothCommitmentsAsync(NLightningTestNode node, ChannelId channelId,
                                                                     HtlcDirection direction, CancellationToken ct) =>
        Poll.ForAsync(() => GetHtlcs(node, channelId).FirstOrDefault(h => h.Direction == direction
                                                                        && h.IsInCommit(CommitmentSide.Local)
                                                                        && h.IsInCommit(CommitmentSide.Remote)),
                      Timeout, $"{direction} HTLC in both commitments of {channelId}", ct);

    /// <summary>LND <paramref name="lnd"/> lists the channel closed with <paramref name="closeType"/> and our txid.</summary>
    public async Task AssertLndClosedAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                           OpenChannelClientSubscriptionResponse channel, uint256 commitmentTxId,
                                           ChannelCloseSummary.Types.ClosureType closeType, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var closed = await lnd.LightningClient.ClosedChannelsAsync(new ClosedChannelsRequest(),
                                                                      cancellationToken: ct);
            var ours = closed.Channels.FirstOrDefault(c => c.ChannelPoint == channel.ChannelPoint());
            if (ours is not null)
            {
                Console.WriteLine($"LND closed the channel: {ours.CloseType}, closing tx {ours.ClosingTxHash}");
                Assert.Equal(closeType, ours.CloseType);
                Assert.Equal(commitmentTxId.ToString(), ours.ClosingTxHash);
                return true;
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return false;
        }, Timeout, $"{lnd.LocalAlias} lists the channel as {closeType}", ct);
    }

    #endregion

    #region Payments

    public static async Task CancelHoldInvoiceQuietlyAsync(LNDNodeConnection node, byte[] paymentHash)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LndTestHelpers.CancelInvoiceAsync(node, paymentHash, timeoutCts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not cancel the hold invoice: {e.Message}");
        }
    }

    /// <summary>We pay <paramref name="lnd"/> <paramref name="amountSat"/> and wait until both sides settled.</summary>
    public static async Task PayLndAsync(NLightningTestNode node, LNDNodeConnection lnd, ChannelId channelId,
                                         string channelPoint, long amountSat, CancellationToken ct)
    {
        var invoice = await LndTestHelpers.AddInvoiceAsync(lnd, amountSat * 1_000, [], ct, "o7 we pay lnd");
        var payment = await node.PayInvoiceAsync(invoice.PaymentRequest, ct);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await WaitSettledWithLndAsync(node, lnd, channelId, channelPoint, ct);
    }

    /// <summary><paramref name="lnd"/> pays our invoice of <paramref name="amountSat"/>, and both sides settle.</summary>
    public static async Task LndPaysUsAsync(NLightningTestNode node, LNDNodeConnection lnd, ulong chanId,
                                            ChannelId channelId, string channelPoint, long amountSat,
                                            CancellationToken ct)
    {
        var invoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "o7 lnd pays us", ct);
        Payment? payment = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            // Right after an open or a settled payment LND may not route to us yet (no route); try again
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            payment = await LndTestHelpers.SendPaymentV2Async(lnd, LndTestHelpers.PinnedPayment(invoice.Bolt11,
                                                                  [chanId]), ct);
            if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
                break;

            Console.WriteLine($"{lnd.LocalAlias}'s payment attempt {attempt} failed: {payment.FailureReason}");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment!.Status);
        await WaitSettledWithLndAsync(node, lnd, channelId, channelPoint, ct);
    }

    private static async Task WaitSettledWithLndAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                                      ChannelId channelId, string channelPoint, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && theirs is not null && theirs.PendingHtlcs.Count == 0
                && theirs.LocalBalance == (long)ours.RemoteBalance.Satoshi;
        }, Timeout, "the payment settled with LND", ct);
    }

    #endregion
}