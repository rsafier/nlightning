using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Fixtures;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Closes against Eclair 0.14.3 beyond the legacy cooperative close of <see cref="EclairInteropTests"/> (NL-554), on the
/// dual-funded anchors channels of our defaults: (a) we force close (<c>forceclosechannel</c>): Eclair sees our
/// commitment and closes its side, and once Eclair's 720-block <c>to_self_delay</c> has passed our <c>to_local</c> sweep
/// pays our wallet; (b) Eclair force closes (<c>forceclose</c>): we record its commitment as the peer's and sweep our
/// CSV-1 <c>to_remote</c> into our wallet; (c) and (d) <c>option_simple_close</c> (<c>closing_complete</c>/
/// <c>closing_sig</c>, BOLT 2 since N11), which Eclair offers and our node turns on (it is off by default): we close,
/// and Eclair closes.
/// </summary>
/// <remarks>Run with <c>scripts/run-interop.sh eclair Release -class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairCloseTests</c>.</remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairCloseTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 12 * 60 * 1_000;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_paidToEclair = LightningMoney.Satoshis(300_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(120);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;

    public EclairCloseTests(EclairFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        if (DockerDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            await DockerDiagnostics.DumpContainerLogsAsync([EclairFixture.EclairContainerName], 400);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) Our <c>forceclosechannel</c> on the dual-funded channel we opened (300k sat paid to Eclair first): our anchors
    /// commitment confirms and is recorded as our local commitment with our anchor and <c>to_local</c> rows; Eclair
    /// closes its side; after Eclair's 720-block <c>to_self_delay</c> our <c>to_local</c> is swept and confirmed into our
    /// wallet, which grows by that output less the sweep fee.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ADualFundedChannel_When_WeForceClose_Then_EclairClosesAndOurToLocalIsSweptAfterTheDelay()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await BuildAsync("nltg-eclair-fc-we", ct);
        var model = Model(session);
        var toSelfDelay = model.ChannelParams.Remote.ToSelfDelay;
        Assert.Equal(EclairFixture.EclairDefaultToRemoteDelayBlocks, toSelfDelay);

        // Act
        var closed = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                         session, new ForceCloseChannelClientRequest(session.ChannelId), ct);

        // Assert: our commitment confirms and is recorded as ours
        Console.WriteLine($"[nltg] forceclosechannel: {closed.State} {closed.Status} {closed.CommitmentTxId}");
        Assert.NotNull(closed.CommitmentTxId);
        var commitmentTxId = new uint256((byte[])closed.CommitmentTxId.Value);
        var commitment = await ConfirmAsync(session, commitmentTxId, ct);
        var close = await AnchorsHarness.WaitForCloseAsync(session.Node, session.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        var toLocal = await WaitRowAsync(session, close.CommitmentTransactionId, OutputDescriptorKind.DelayedToLocal,
                                         ct);
        await WaitRowAsync(session, close.CommitmentTransactionId, OutputDescriptorKind.OurAnchor, ct);
        await WaitEclairClosedAsync(session, ct);
        var toLocalValue = commitment.Outputs[(int)toLocal.OutputIndex].Value;
        Console.WriteLine($"[proof] our commitment {commitmentTxId}: to_local {toLocalValue} at output "
                        + $"{toLocal.OutputIndex}, spendable after {toSelfDelay} blocks");

        // ...and the to_local sweep after the delay pays our wallet
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        await MineManyAsync(session, toSelfDelay, ct);
        var sweep = await MineUntilResolvedAsync(session, toLocal, ct);
        await Poll.UntilAsync(() => Task.FromResult(AnchorsHarness.WalletBalance(session.Node) - walletBefore
                                                 >= LightningMoney.Satoshis((ulong)toLocalValue.Satoshi - 5_000)),
                              s_stepTimeout, "our wallet holds our to_local less the sweep fee", ct);
        Console.WriteLine($"[proof] to_local swept by {sweep}; wallet +"
                        + $"{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");
    }

    /// <summary>
    /// (b) Eclair's <c>forceclose</c> on the dual-funded channel we opened (300k sat paid to Eclair first): Eclair's
    /// commitment confirms, we record it as the peer's commitment and sweep our CSV-1 <c>to_remote</c> (our balance less
    /// the commitment fee and both anchors, which the funder pays) into our wallet, which grows by that output less the
    /// sweep fee; our channel is closed and Eclair lists it closing or closed.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ADualFundedChannel_When_EclairForceCloses_Then_WeSweepOurToRemoteIntoOurWallet()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await BuildAsync("nltg-eclair-fc-eclair", ct);
        var ours = await session.GetOurChannelAsync(ct);

        // Act
        var answer = await session.Eclair.ForceCloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] forceclose: {answer?.ToJsonString()}");

        // Assert: Eclair's commitment is recorded as the peer's and our to_remote is swept
        var close = await MineUntilAsync(session, async () =>
        {
            using var scope = session.Node.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>()
                              .OnchainResolutionDbRepository.GetCloseAsync(session.ChannelId);
        }, "Eclair's commitment confirmed and recorded", ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        var commitment = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(
                             new uint256((byte[])close.CommitmentTransactionId), true, ct);
        var toRemote = await WaitRowAsync(session, close.CommitmentTransactionId, OutputDescriptorKind.PaymentToRemote,
                                          ct);
        var toRemoteValue = commitment.Outputs[(int)toRemote.OutputIndex].Value;
        Console.WriteLine($"[proof] Eclair's commitment {commitment.GetHash()}: our to_remote {toRemoteValue} at "
                        + $"output {toRemote.OutputIndex}; our balance was {ours.LocalBalance.Satoshi} sat");
        // We funded the channel: our output is our balance less the commitment fee and both anchors
        Assert.InRange(toRemoteValue.Satoshi, (long)ours.LocalBalance.Satoshi - 10_000,
                       (long)ours.LocalBalance.Satoshi - 2 * 330);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);
        var sweep = await MineUntilResolvedAsync(session, toRemote, ct);
        await Poll.UntilAsync(() => Task.FromResult(AnchorsHarness.WalletBalance(session.Node) - walletBefore
                                                 >= LightningMoney.Satoshis((ulong)toRemoteValue.Satoshi - 5_000)),
                              s_stepTimeout, "our wallet holds our to_remote less the sweep fee", ct);
        Console.WriteLine($"[proof] to_remote swept by {sweep}; wallet +"
                        + $"{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");
    }

    /// <summary>
    /// (c) <c>option_simple_close</c> negotiated with Eclair: our <c>closechannel</c> sends <c>shutdown</c> and
    /// <c>closing_complete</c>; Eclair signs ours (<c>closing_sig</c>) and we sign its; a BOLT 3 simple-close
    /// transaction (version 2, sequence 0xFFFFFFFD) is in the mempool, and after 6 blocks both ends list the channel
    /// closed and our wallet holds our balance less the fee we pay.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_SimpleCloseNegotiated_When_WeClose_Then_ClosingCompleteBothWaysAndBothClose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await BuildAsync("nltg-eclair-simple-we", ct, simpleClose: true);
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);

        // Act
        var closed = await HandleAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                         session, new CloseChannelClientRequest(session.ChannelId)
                         {
                             WaitSeconds = (uint)s_stepTimeout.TotalSeconds
                         }, ct);

        // Assert
        Assert.Equal(ChannelState.Closing, closed.State);
        await WaitForSimpleCloseExchangeAsync(session, ct);
        var closingTx = await AssertSimpleClosingTxAsync(session, ct);
        await _fixture.MineAndWaitAsync(6, [session.Node], ct);
        await WaitClosedBothSidesAsync(session, ct);
        var received = AnchorsHarness.WalletBalance(session.Node) - walletBefore;
        Console.WriteLine($"[proof] simple close {closingTx.GetHash()}: our balance {ours.LocalBalance.Satoshi} sat, "
                        + $"wallet +{received.Satoshi} sat");
        Assert.True(received.Satoshi + 10_000 >= ours.LocalBalance.Satoshi && received <= ours.LocalBalance,
                    $"our wallet got {received.Satoshi} sat for a balance of {ours.LocalBalance.Satoshi} sat");
    }

    /// <summary>
    /// (d) <c>option_simple_close</c> negotiated, Eclair closes (<c>close</c>): we answer its <c>shutdown</c>, sign its
    /// <c>closing_complete</c> and send ours; the simple-close transaction confirms, both list the channel closed, and
    /// our wallet holds our whole balance or our own transaction's output.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_SimpleCloseNegotiated_When_EclairCloses_Then_WeSignAndBothClose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await BuildAsync("nltg-eclair-simple-eclair", ct, simpleClose: true);
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);

        // Act
        var answer = await session.Eclair.CloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] close: {answer?.ToJsonString()}");

        // Assert
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("Signed the peer's closing transaction") >= 1),
                              s_stepTimeout, "we signed Eclair's closing_complete", ct);
        var closingTx = await AssertSimpleClosingTxAsync(session, ct);
        await _fixture.MineAndWaitAsync(6, [session.Node], ct);
        await WaitClosedBothSidesAsync(session, ct);
        var received = AnchorsHarness.WalletBalance(session.Node) - walletBefore;
        Console.WriteLine($"[proof] Eclair's simple close {closingTx.GetHash()}: our balance "
                        + $"{ours.LocalBalance.Satoshi} sat, wallet +{received.Satoshi} sat");
        Assert.True(received.Satoshi + 10_000 >= ours.LocalBalance.Satoshi && received <= ours.LocalBalance,
                    $"our wallet got {received.Satoshi} sat for a balance of {ours.LocalBalance.Satoshi} sat");
        Assert.Equal(0, session.Node.CountLogLines("closing_signed for channel"));
    }

    /// <summary>
    /// The dual-funded channel we open to Eclair (plain <c>openchannel</c>, 1M sat) with 300k sat paid to Eclair, so
    /// both sides have an output; with <paramref name="simpleClose"/> our node offers <c>option_simple_close</c> (and
    /// its dependency <c>option_shutdown_anysegwit</c>).
    /// </summary>
    private async Task<EclairChannelSession> BuildAsync(string nodeName, CancellationToken ct, bool simpleClose = false)
    {
        _session = await EclairChannelSession.BuildOurFundedAsync(
                       _fixture, nodeName, s_capacity, null, ct,
                       configureNodeOptions: simpleClose
                                                 ? o =>
                                                 {
                                                     o.Features.OptionSimpleClose = FeatureSupport.Optional;
                                                     o.Features.BeyondSegwitShutdown = FeatureSupport.Optional;
                                                 }
        : null);
        if (simpleClose)
        {
            var peer = _session.Node.PeerManager.GetPeer(_session.EclairPubKey);
            Assert.NotNull(peer);
            Assert.NotEqual(FeatureSupport.No, peer.NegotiatedFeatures.OptionSimpleClose);
        }

        await _session.AssertWePayEclairAsync(s_paidToEclair, ct);
        return _session;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(EclairChannelSession session,
                                                                         TRequest request, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    private static Domain.Channels.Models.ChannelModel Model(EclairChannelSession session) =>
        session.Node.ChannelMemoryRepository.TryGetChannel(session.ChannelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{session.Node.Name} has no channel {session.ChannelId}");

    /// <summary>Mines one block at a time until <paramref name="txId"/> is confirmed; returns the transaction.</summary>
    private async Task<Transaction> ConfirmAsync(EclairChannelSession session, uint256 txId, CancellationToken ct)
    {
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(txId),
                              s_stepTimeout, $"{txId} in the mempool", ct);
        await _fixture.MineAndWaitAsync(1, [session.Node], ct);
        var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(txId, ct);
        Assert.True(info.Confirmations >= 1, $"{txId} is not confirmed");
        return info.Transaction;
    }

    private async Task<T> MineUntilAsync<T>(EclairChannelSession session, Func<Task<T?>> probe, string what,
                                            CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + s_stepTimeout;
        while (true)
        {
            if (await probe() is { } found)
                return found;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Not in time: {what}");

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>Mines <paramref name="blocks"/> blocks in batches, waiting for our node and Eclair after each.</summary>
    private async Task MineManyAsync(EclairChannelSession session, int blocks, CancellationToken ct)
    {
        for (var left = blocks; left > 0; left -= 100)
        {
            await _fixture.Chain.MineAsync(Math.Min(100, left), ct);
            await _fixture.WaitAllAtTipAsync([session.Node], ct, TimeSpan.FromMinutes(3));
        }
    }

    /// <summary>
    /// Mines one block at a time until our node recorded a resolving transaction for <paramref name="row"/>'s output and
    /// it confirmed; returns its txid.
    /// </summary>
    private async Task<uint256> MineUntilResolvedAsync(EclairChannelSession session, OutputResolutionModel row,
                                                       CancellationToken ct)
    {
        var resolving = await MineUntilAsync(session, async () =>
        {
            var rows = await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId);
            var current = rows.FirstOrDefault(r => r.TransactionId == row.TransactionId
                                                && r.OutputIndex == row.OutputIndex);
            return current?.ResolvingTransactionId is { } txId ? new uint256((byte[])txId) : null;
        }, $"a resolving transaction for output {row.OutputIndex}", ct);
        await MineUntilAsync(session, async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(resolving, ct);
                return info.Confirmations >= 1 ? info : null;
            }
            catch (NBitcoin.RPC.RPCException)
            {
                return null;
            }
        }, $"{resolving} confirmed", ct);
        return resolving;
    }

    private static async Task<OutputResolutionModel> WaitRowAsync(EclairChannelSession session, TxId commitment,
                                                                  OutputDescriptorKind kind, CancellationToken ct) =>
        await AnchorsHarness.WaitForRowAsync(session.Node, session.ChannelId,
                                             r => r.TransactionId == commitment && r.Descriptor == kind,
                                             $"our {kind} row", ct);

    /// <summary>Eclair lists the channel <c>CLOSING</c> or <c>CLOSED</c> (or in <c>closedchannels</c>).</summary>
    private static async Task WaitEclairClosedAsync(EclairChannelSession session, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var channel = await session.Eclair.ChannelAsync(session.ChannelIdHex, ct);
            var state = channel?["state"]?.GetValue<string>();
            if (state is "CLOSING" or "CLOSED")
                return true;

            var closed = await session.Eclair.ClosedChannelsAsync(session.Node.NodeIdHex, ct);
            return closed.Any(c => c?["channelId"]?.GetValue<string>() == session.ChannelIdHex);
        }, s_stepTimeout, "Eclair lists the channel closing or closed", ct);

    /// <summary>Our channel Closed (or gone) and Eclair's <c>CLOSED</c> (or in <c>closedchannels</c>).</summary>
    private static async Task WaitClosedBothSidesAsync(EclairChannelSession session, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = (await session.Node.ListChannelsAsync(ct)).Channels
                                                                   .FirstOrDefault(c => c.ChannelId
                                                                                     == session.ChannelId);
            return ours is null || ours.State == ChannelState.Closed;
        }, s_stepTimeout, "our channel is Closed", ct);
        await Poll.UntilAsync(async () =>
        {
            var channel = await session.Eclair.ChannelAsync(session.ChannelIdHex, ct);
            if (channel?["state"]?.GetValue<string>() == "CLOSED")
                return true;

            var closed = await session.Eclair.ClosedChannelsAsync(session.Node.NodeIdHex, ct);
            return closed.Any(c => c?["channelId"]?.GetValue<string>() == session.ChannelIdHex);
        }, s_stepTimeout, "Eclair lists the channel closed", ct);
    }

    /// <summary>
    /// Both sides' <c>closing_complete</c> were signed by the other (our log: the peer signed ours, we signed theirs).
    /// </summary>
    private static async Task WaitForSimpleCloseExchangeAsync(EclairChannelSession session, CancellationToken ct) =>
        await Poll.UntilAsync(() => Task.FromResult(
                                  session.Node.CountLogLines("The peer signed our closing transaction") >= 1
                               && session.Node.CountLogLines("Signed the peer's closing transaction") >= 1),
                              s_stepTimeout, "closing_complete signed both ways", ct);

    /// <summary>
    /// A transaction spending the funding output in bitcoind's mempool, with the BOLT 3 simple-close shape (version 2,
    /// one input with sequence 0xFFFFFFFD).
    /// </summary>
    private async Task<Transaction> AssertSimpleClosingTxAsync(EclairChannelSession session, CancellationToken ct)
    {
        var funding = Model(session).FundingOutput!;
        var fundingOutPoint = new OutPoint(new uint256((byte[])funding.TransactionId!.Value), funding.Index!.Value);
        var closingTx = await Poll.ForAsync(async () =>
        {
            foreach (var txid in await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct))
            {
                var mempoolTx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txid, true, ct);
                if (mempoolTx.Inputs.Any(i => i.PrevOut == fundingOutPoint))
                    return mempoolTx;
            }

            return null;
        }, s_stepTimeout, "a closing transaction in the mempool", ct);
        Console.WriteLine($"[proof] closing transaction {closingTx.GetHash()}: version {closingTx.Version}, sequence "
                        + $"{(uint)closingTx.Inputs[0].Sequence:X8}, outputs "
                        + string.Join(", ", closingTx.Outputs.Select(o => o.Value.Satoshi)));
        Assert.Equal(2U, closingTx.Version);
        Assert.Equal(0xFFFFFFFDU, (uint)Assert.Single(closingTx.Inputs).Sequence);
        Assert.Equal(0, session.Node.CountLogLines("closing_signed for channel"));
        return closingTx;
    }
}