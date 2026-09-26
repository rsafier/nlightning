using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O2 (classification and fail-the-channel broadcast), against bitcoind and LND david:
/// <list type="bullet">
///   <item><c>forceclosechannel</c> on an idle channel with a push: our latest commitment (both signatures) is in
///   bitcoind's mempool with its <c>LocalCommitment</c> broadcast row (NL-271); one block later our channel is
///   <c>OnchainResolving</c> with a <c>LocalCommitment</c> close whose <c>to_local</c> is recorded for resolution
///   (<c>pendingsweeps</c>), and LND lists the channel as a pending force close.</item>
///   <item>LND force-closes: its commitment confirms, and we classify it <c>RemoteCommitment</c>, fail the channel into
///   <c>OnchainResolving</c> and record our <c>to_remote</c> (NL-272).</item>
/// </list>
/// </summary>
/// <remarks>Run with <c>scripts/run-onchain.sh</c> (own process, own fixture).</remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainO2Tests : IAsyncLifetime
{
    private const long FundingSat = 1_000_000;
    private const long PushSat = 200_000;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public OnchainO2Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "onchain-o2");
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_WeForceClose_Then_LndSeesForceCloseAndChannelResolving()
    {
        // Arrange: an idle channel from us to david with a push
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        var opened = await OpenUsableChannelAsync(node, david, ct);

        // Act
        var forceClose = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                             node, new ForceCloseChannelClientRequest(opened.ChannelId), ct);

        // Assert: broadcast, Failed, and the commitment's broadcast row stored with its number
        Console.WriteLine($"forceclosechannel: {forceClose.Status} {forceClose.CommitmentTxId}");
        Assert.Equal("Broadcast", forceClose.Status);
        Assert.Equal(ChannelState.Failed, forceClose.State);
        Assert.NotNull(forceClose.CommitmentTxId);
        var commitmentTxId = forceClose.CommitmentTxId.Value;
        var displayTxId = new uint256((byte[])commitmentTxId);
        Assert.Contains(displayTxId, await _fixture.Bitcoin.GetRawMempoolAsync(ct));
        var row = await GetBroadcastAsync(node, commitmentTxId);
        Assert.Equal(BroadcastPurpose.LocalCommitment, row.Purpose);
        Assert.Equal(0UL, row.CommitmentNumber);

        // Act: one block
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);

        // Assert: resolving on chain, our commitment recorded as the close with our to_local to resolve
        var sweeps = await WaitResolvingAsync(node, opened.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, sweeps.CloseKind);
        Assert.Equal(commitmentTxId, sweeps.CommitmentTxId);
        Assert.Equal(0UL, sweeps.CommitmentNumber);
        var toLocal = Assert.Single(sweeps.Outputs);
        Assert.Equal(OutputDescriptorKind.DelayedToLocal, toLocal.Descriptor);
        Assert.Equal(commitmentTxId, toLocal.TransactionId);
        Assert.InRange((long)toLocal.AmountSat!.Value, FundingSat - PushSat - 20_000, FundingSat - PushSat);
        Assert.Equal(BroadcastState.Confirmed, (await GetBroadcastAsync(node, commitmentTxId)).State);

        // LND lists it as a pending force close by its peer with our commitment
        await Poll.UntilAsync(async () =>
        {
            var pending = await david.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                           cancellationToken: ct);
            var ours = pending.PendingForceClosingChannels.FirstOrDefault(
                           c => c.Channel.ChannelPoint == opened.ChannelPoint());
            if (ours is not null)
            {
                Console.WriteLine($"LND pending force close: closing tx {ours.ClosingTxid}");
                Assert.Equal(displayTxId.ToString(), ours.ClosingTxid);
                return true;
            }

            var waiting = pending.WaitingCloseChannels.FirstOrDefault(
                              c => c.Channel.ChannelPoint == opened.ChannelPoint());
            if (waiting is not null)
                Console.WriteLine($"LND still waits for the close: {waiting.ClosingTxid}");
            return false;
        }, s_timeout, "LND lists the channel as pending force closed", ct);
    }

    [Fact]
    public async Task Given_LndForceCloses_Then_WeDetectRemoteCommit()
    {
        // Arrange: an idle channel from us to david with a push
        var ct = TestContext.Current.CancellationToken;
        var node = _node!;
        var david = _fixture.GetLndNode("david");
        var opened = await OpenUsableChannelAsync(node, david, ct);
        var parts = opened.ChannelPoint().Split(':');

        // Act: david force-closes; its commitment confirms
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = david.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint
            {
                FundingTxidStr = parts[0],
                OutputIndex = uint.Parse(parts[1])
            },
            Force = true
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);
        // LND's ClosePending txid is in internal byte order, like ours
        var lndCommitment = new TxId(pending.Txid.ToByteArray());
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.GetRawMempoolAsync(ct))
                                 .Contains(new uint256((byte[])lndCommitment)), s_timeout,
                              "david's commitment in the mempool", ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);

        // Assert: we classify its commitment and record our to_remote (funding - push - the fee we pay as funder)
        var sweeps = await WaitResolvingAsync(node, opened.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, sweeps.CloseKind);
        Assert.Equal(lndCommitment, sweeps.CommitmentTxId);
        var toRemote = Assert.Single(sweeps.Outputs);
        Assert.Equal(OutputDescriptorKind.PaymentToRemote, toRemote.Descriptor);
        Assert.InRange((long)toRemote.AmountSat!.Value, FundingSat - PushSat - 20_000, FundingSat - PushSat);
        var confirmed = await _fixture.Bitcoin.GetRawTransactionInfoAsync(new uint256((byte[])lndCommitment), ct);
        Assert.Equal(toRemote.AmountSat!.Value,
                     (ulong)confirmed.Transaction.Outputs[(int)toRemote.OutputIndex].Value.Satoshi);
        Assert.Equal(ChannelState.OnchainResolving, (await node.GetChannelAsync(opened.ChannelId, ct)).State);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["david"]);

        if (_node is not null)
            await _node.DisposeAsync();

        GC.SuppressFinalize(this);
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(NLightningTestNode node,
                                                                                    LNDNodeConnection david,
                                                                                    CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await node.ConnectToAsync(david, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress,
                                                                              LightningMoney.Satoshis(FundingSat))
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000),
            PushAmount = LightningMoney.Satoshis(PushSat)
        }, ct);
        Console.WriteLine($"Opened channel {opened.ChannelId} ({opened.ChannelPoint()}) to david");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(opened.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(david, opened.ChannelPoint(), ct);
            if (ours.IsUsable() && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [node], ct);
            return false;
        }, s_timeout, $"channel {opened.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [node], ct);
        return opened;
    }

    /// <summary><c>pendingsweeps</c> until the channel is resolving on chain; returns its entry.</summary>
    private static async Task<PendingSweepChannelInfo> WaitResolvingAsync(NLightningTestNode node, ChannelId channelId,
                                                                          CancellationToken ct)
    {
        var entry = await Poll.ForAsync(async () =>
        {
            var sweeps = await HandleAsync<PendingSweepsClientRequest, PendingSweepsClientResponse>(
                             node, new PendingSweepsClientRequest { ChannelId = channelId }, ct);
            return sweeps.Channels.FirstOrDefault(c => c.State == ChannelState.OnchainResolving);
        }, s_timeout, $"channel {channelId} resolving on chain", ct);
        Console.WriteLine($"pendingsweeps: {entry.CloseKind} {entry.CommitmentTxId}, "
                        + string.Join(", ", entry.Outputs.Select(o => $"{o.OutputIndex} {o.Descriptor} {o.State} "
                                                                    + $"{o.AmountSat} sat")));
        return entry;
    }

    private static async Task<Domain.Onchain.Models.BroadcastTransactionModel> GetBroadcastAsync(
        NLightningTestNode node, TxId txId)
    {
        using var scope = node.Services.CreateScope();
        var broadcast = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                                   .GetByTransactionIdAsync(txId);
        Assert.NotNull(broadcast);
        return broadcast;
    }

    /// <summary>A client command through the daemon's handler, as <c>nltg</c> sends it.</summary>
    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }
}