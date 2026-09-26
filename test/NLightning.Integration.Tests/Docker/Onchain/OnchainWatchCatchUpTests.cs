using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Application.Onchain.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Events;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Fixtures;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Utils;

/// <summary>
/// NL-311 proof (BOLT 5: "MUST monitor the blockchain for transactions that spend any output that is not irrevocably
/// resolved"): david force-closes an idle channel with a push, and our node saves the <c>to_remote</c> row and its watch
/// but never tracks the watch (a test decorator of <see cref="IOutpointWatcher"/> drops the resolution watches, which
/// is what a crash between the save and <c>TrackWatchedOutpoint</c> leaves). Our sweep of <c>to_remote</c> confirms in
/// a block the chain monitor processes without the watch, so the row stays <c>Broadcast</c>. After a restart (the
/// monitor replays only its last processed block, which does not hold the sweep) the executor's first round catches
/// the saved watch up and records the sweep's spend at its block.
/// </summary>
/// <remarks>Run with <c>scripts/run-onchain.sh</c> or the in-container runner (NL-276).</remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainWatchCatchUpTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public OnchainWatchCatchUpTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        // Started by the test, with its decorator
        _node = await NLightningTestNode.CreateAsync(_fixture, "onchain-nl311");
    }

    [Fact]
    public async Task Given_ResolutionWatchSavedButNeverTracked_When_Restarted_Then_TheSweepMinedMeanwhileIsResolved()
    {
        // Arrange: our node never tracks a resolution watch; a usable channel to david with a push
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        var dropped = new List<WatchedOutpointModel>();
        Node.ConfigureServices = services => services.AddSingleton<IOutpointWatcher>(
                                     sp => new ResolutionWatchDroppingWatcher(sp.GetRequiredService<IBlockchainMonitor>(),
                                                                              dropped));
        await Node.StartAsync(ct);
        var channel = await OpenUsableChannelAsync(david, ct);

        // Act: david force-closes; we classify it and sweep to_remote, and the sweep confirms without the watch
        var commitmentTxId = await ForceCloseAsync(david, channel, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
        var toRemote = await WaitForOutputAsync(channel.ChannelId,
                                                o => o is
                                                {
                                                    Descriptor: OutputDescriptorKind.PaymentToRemote,
                                                    ResolvingTransactionId: not null
                                                }, "our to_remote sweep saved", ct);
        Assert.Contains(dropped, w => w.TransactionId == toRemote.TransactionId
                                   && w.OutputIndex == toRemote.OutputIndex);
        var sweepTxId = new uint256((byte[])toRemote.ResolvingTransactionId!.Value);

        // The sweep is kept out of the next block, so this process's first block round of the channel (whose saved
        // watch catch-up also covers a race in a running process) is over before the sweep confirms
        await _fixture.Bitcoin.SendCommandAsync("prioritisetransaction", ct, sweepTxId.ToString(), 0, -100_000_000);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
        await Node.Services.GetRequiredService<IOnchainResolutionExecutor>().WhenIdleAsync();
        Assert.Contains(sweepTxId, await _fixture.Bitcoin.GetRawMempoolAsync(ct));
        await _fixture.Bitcoin.SendCommandAsync("prioritisetransaction", ct, sweepTxId.ToString(), 0, 100_000_000);
        var sweepHeight = await MineUntilConfirmedAsync(sweepTxId, david, ct);
        Assert.Equal(commitmentTxId, new uint256((byte[])toRemote.TransactionId));
        Console.WriteLine($"Sweep {sweepTxId} of to_remote vout {toRemote.OutputIndex} confirmed at {sweepHeight}");

        // One more block, so the block the monitor replays at startup is not the sweep's
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
        await Node.Services.GetRequiredService<IOnchainResolutionExecutor>().WhenIdleAsync();
        var missed = await GetOutputAsync(channel.ChannelId, toRemote.OutputIndex);
        Assert.Equal(OutputResolutionState.Broadcast, missed?.State);

        // Act: restart without the decorator (the monitor loads and tracks every saved watch)
        await Node.StopAsync();
        Node.ConfigureServices = null;
        await Node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [Node], ct);

        // Assert: the executor's first round found the sweep in its block and recorded it on the watch
        var resolved = await WaitForOutputAsync(channel.ChannelId,
                                                o => o.OutputIndex == toRemote.OutputIndex
                                                  && o.TransactionId == toRemote.TransactionId
                                                  && o.State >= OutputResolutionState.Resolved,
                                                "the to_remote sweep caught up after the restart", ct);
        Assert.Equal(sweepHeight, resolved.ResolvedHeight);
        using var scope = Node.Services.CreateScope();
        var watch = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().WatchedOutpointDbRepository
                               .GetAsync(toRemote.TransactionId, toRemote.OutputIndex);
        Assert.NotNull(watch);
        Assert.Equal(sweepTxId, new uint256((byte[])watch.SpentByTransactionId!.Value));
        Assert.Equal(sweepHeight, watch.SpentAtHeight);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["david"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LNDNodeConnection peer,
                                                                                    CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await Node.ConnectToAsync(peer, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [Node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [Node], ct);
        return channel;
    }

    /// <summary>LND <c>CloseChannel { force = true }</c>; returns the commitment's txid once it is broadcast.</summary>
    private static async Task<uint256> ForceCloseAsync(LNDNodeConnection lnd,
                                                       OpenChannelClientSubscriptionResponse channel,
                                                       CancellationToken ct)
    {
        var parts = channel.ChannelPoint().Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
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

    /// <summary>Mines one block at a time until <paramref name="txId"/> is confirmed; returns its block height.</summary>
    private async Task<uint> MineUntilConfirmedAsync(uint256 txId, LNDNodeConnection lnd, CancellationToken ct) =>
        (await Poll.ForAsync(async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(txId, ct);
                if (info.Confirmations >= 1)
                    return new ConfirmedAt((uint)(await _fixture.Bitcoin.GetBlockCountAsync(ct) - info.Confirmations + 1));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not broadcast yet
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [Node], ct);
            return null;
        }, s_timeout, $"transaction {txId} confirmed", ct)).Height;

    private async Task<OutputResolutionModel> WaitForOutputAsync(ChannelId channelId,
                                                                 Func<OutputResolutionModel, bool> predicate,
                                                                 string description, CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            using var scope = Node.Services.CreateScope();
            var outputs = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                                     .GetOutputsByChannelIdAsync(channelId);
            return outputs.FirstOrDefault(predicate);
        }, s_timeout, description, ct);

    private async Task<OutputResolutionModel?> GetOutputAsync(ChannelId channelId, uint vout)
    {
        using var scope = Node.Services.CreateScope();
        var outputs = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                                 .GetOutputsByChannelIdAsync(channelId);
        return outputs.FirstOrDefault(o => o.OutputIndex == vout);
    }

    private sealed record ConfirmedAt(uint Height);

    /// <summary>
    /// The chain monitor as <see cref="IOutpointWatcher"/> (and <see cref="IChainBroadcaster"/>) for the on-chain
    /// watcher and executor, except that <see cref="WatchedOutpointPurpose.ResolutionOutput"/> watches are never
    /// tracked: what a crash between their save and the tracking leaves until the next start.
    /// </summary>
    private sealed class ResolutionWatchDroppingWatcher(IBlockchainMonitor monitor, List<WatchedOutpointModel> dropped)
        : IOutpointWatcher, IChainBroadcaster
    {
        public event EventHandler<OutpointSpentEventArgs>? OnWatchedOutpointSpent
        {
            add => monitor.OnWatchedOutpointSpent += value;
            remove => monitor.OnWatchedOutpointSpent -= value;
        }

        public event EventHandler<BlockDisconnectedEventArgs>? OnBlockDisconnected
        {
            add => monitor.OnBlockDisconnected += value;
            remove => monitor.OnBlockDisconnected -= value;
        }

        public Task WatchOutpointAsync(WatchedOutpointModel watchedOutpoint) =>
            monitor.WatchOutpointAsync(watchedOutpoint);

        public void TrackWatchedOutpoint(WatchedOutpointModel watchedOutpoint)
        {
            if (watchedOutpoint.Purpose == WatchedOutpointPurpose.ResolutionOutput)
            {
                lock (dropped)
                    dropped.Add(watchedOutpoint);
                Console.WriteLine($"[nl311] not tracking resolution watch {watchedOutpoint.OutputIndex} of "
                                + $"{new uint256((byte[])watchedOutpoint.TransactionId)}");
                return;
            }

            monitor.TrackWatchedOutpoint(watchedOutpoint);
        }

        public Task<bool> PublishAsync(BroadcastTransactionModel transaction) => monitor.PublishAsync(transaction);

        public Task<bool> SaveAndPublishAsync(BroadcastTransactionModel transaction) =>
            monitor.SaveAndPublishAsync(transaction);
    }
}