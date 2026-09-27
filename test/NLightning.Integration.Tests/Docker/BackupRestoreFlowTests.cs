using Lnrpc;
using LNUnit.LND;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Channels.Backup;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Onchain;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Static channel backup and recovery (wave rf1 lane R1), against LND david: we open a channel (anchors, or
/// <c>option_static_remotekey</c>), pay over it, export the backup through the daemon's <c>exportchanbackup</c>
/// handler, lose the whole database (the node restarts on the same key file with an empty one), restore the backup
/// through <c>restorechanbackup</c>, and LND force-closes on our data-loss <c>channel_reestablish</c> (BOLT 2
/// <c>next_commitment_number</c> 0). We never broadcast a commitment of our own; LND's commitment is recorded as one we
/// cannot rebuild (<see cref="ChannelCloseKind.FutureCommitment"/>) and our <c>to_remote</c> (our whole balance: no
/// HTLC is in flight) is swept to the new wallet (after one block with anchors: CSV 1).
/// </summary>
/// <remarks>Own on-chain fixture (it closes channels): run with
/// <c>scripts/run-onchain.sh 1 Release -class NLightning.Integration.Tests.Docker.BackupRestoreFlowTests</c>.</remarks>
[Collection(OnchainRegtestCollection.Name)]
public class BackupRestoreFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(180);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly AnchorsHarness _harness;

    public BackupRestoreFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Given_ABackupAndAWipedDatabase_When_Restored_Then_LndForceClosesAndOurToRemoteIsSwept(bool anchors) =>
        RunAsync(anchors, false);

    /// <summary>
    /// LND force-closes while our database is lost and its commitment confirms before <c>restorechanbackup</c> runs
    /// (the chain monitor only sees spends from its height on): the restore finds the spend and our <c>to_remote</c>
    /// is still swept.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Given_LndForceClosedBeforeTheRestore_When_Restored_Then_TheEarlierSpendIsFoundAndOurToRemoteIsSwept(
        bool anchors) =>
        RunAsync(anchors, true);

    private async Task RunAsync(bool anchors, bool peerClosesFirst)
    {
        // Arrange: a used channel and its backup
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        var node = await _harness.CreateNodeAsync((anchors ? "scb-anchors" : "scb-legacy") + (peerClosesFirst ? "-early" : ""),
                                                  ct,
                                                  o => o.Features.OptionAnchors = anchors
                                                                                     ? FeatureSupport.Optional
                                                                                     : FeatureSupport.No,
                                                  n => n.ConfigureServices = AddBackupServices);
        var channel = await OpenChannelAsync(node, david, anchors, ct);
        await AnchorsHarness.PayLndAsync(node, david, channel.ChannelId, channel.ChannelPoint(), 50_000, ct);
        await AnchorsHarness.PayLndAsync(node, david, channel.ChannelId, channel.ChannelPoint(), 20_000, ct);
        var lndBefore = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
        Assert.NotNull(lndBefore);
        Assert.Empty(lndBefore.PendingHtlcs);
        var ourBalanceSat = lndBefore.RemoteBalance;
        Console.WriteLine($"Before the loss: our balance {ourBalanceSat} sat, commitment height "
                        + $"{lndBefore.NumUpdates} updates");
        var export = await HandleAsync<ExportChanBackupClientRequest, ExportChanBackupClientResponse>(
                         node, new ExportChanBackupClientRequest(), ct);
        Assert.Equal([channel.ChannelId], export.ChannelIds);

        // Act 1: the database is lost; the node comes back on the same key file with an empty one
        await node.StopAsync();
        SqliteConnection.ClearAllPools(); // a pooled connection would still reach the deleted file
        node.DeleteFiles();
        Assert.False(File.Exists(node.DatabaseFilePath));
        await node.StartAsync(ct);
        Assert.False(node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out _));
        var walletBefore = AnchorsHarness.WalletBalance(node);
        var channelPoint = channel.ChannelPoint().Split(':');
        var fundingOutPoint = new OutPoint(uint256.Parse(channelPoint[0]), uint.Parse(channelPoint[1]));
        Transaction? lndCommitment = null;
        if (peerClosesFirst)
        {
            // LND force-closes on its own and its commitment is buried before the restore
            lndCommitment = await ForceCloseAsync(david, fundingOutPoint, ct);
            await _harness.MineUntilConfirmedAsync(node, [david], lndCommitment.GetHash(), ct);
            await ChainSync.MineAndWaitAsync(_fixture, 3, [david], [node], ct);
        }

        // Act 2: restorechanbackup
        var restore = await HandleAsync<RestoreChanBackupClientRequest, RestoreChanBackupClientResponse>(
                          node, new RestoreChanBackupClientRequest { Backup = export.Backup }, ct);

        // Assert: a recovery channel, the peer asked to force close (or its earlier close found)
        var restored = Assert.Single(restore.Channels);
        Console.WriteLine($"Restore: {restored.Outcome}: {restored.Detail}");
        Assert.Equal("Restore", restored.Outcome);
        Assert.Equal(anchors, restored.OptionAnchors);
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out var recovery));
        Assert.True(RecoveryChannels.IsRecoveryChannel(recovery!));
        if (peerClosesFirst)
        {
            Assert.Contains("already closed", restored.Detail);
        }
        else
        {
            Assert.True(Assert.Single(restore.Peers).Connected, restore.Peers[0].Error);

            // LND force-closes: its commitment spends the funding output
            lndCommitment = await Poll.ForAsync(async () => await _harness.FindMempoolSpenderAsync(fundingOutPoint, ct),
                                                s_timeout, "LND's commitment in the mempool", ct);
            Console.WriteLine($"LND force-closed with {lndCommitment.GetHash()}");
            await _harness.MineUntilConfirmedAsync(node, [david], lndCommitment.GetHash(), ct);
        }

        Assert.NotNull(lndCommitment);

        // We recorded it as a commitment we cannot rebuild, never our own
        var close = await AnchorsHarness.WaitForCloseAsync(node, channel.ChannelId, ct);
        TxId commitmentTxId = lndCommitment.GetHash().ToBytes();
        Assert.Equal(commitmentTxId, close.CommitmentTransactionId);
        Assert.Equal(ChannelCloseKind.FutureCommitment, close.Kind);
        using (var scope = node.Services.CreateScope())
        {
            var broadcasts = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                                        .BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);
            Assert.DoesNotContain(broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);
        }

        // Our to_remote: our whole balance, swept to the new wallet
        var toRemoteRow = await AnchorsHarness.WaitForRowAsync(
                              node, channel.ChannelId,
                              r => r.TransactionId == commitmentTxId
                                && r.Descriptor == OutputDescriptorKind.PaymentToRemote,
                              "our to_remote row", ct);
        var toRemote = lndCommitment.Outputs[(int)toRemoteRow.OutputIndex];
        Assert.Equal(ourBalanceSat, toRemote.Value.Satoshi);
        var sweepTxId = await _harness.MineUntilResolvingTxAsync(node, [david], channel.ChannelId, commitmentTxId,
                                                                 toRemoteRow.OutputIndex, ct);
        var sweep = await _harness.WaitInMempoolAsync(sweepTxId, ct);
        await _harness.MineUntilConfirmedAsync(node, [david], sweepTxId, ct);
        var input = Assert.Single(sweep.Inputs);
        Assert.Equal(new OutPoint(lndCommitment, toRemoteRow.OutputIndex), input.PrevOut);
        if (anchors)
            Assert.Equal(1u, input.Sequence.Value);
        var fee = await _harness.FeeAsync(sweep, ct);
        Console.WriteLine($"to_remote {toRemote.Value}, sweep {sweep.GetHash()} fee {fee}");
        Assert.True(fee > Money.Zero && fee < toRemote.Value / 100, $"sweep fee {fee}");
        await Poll.UntilAsync(() => (AnchorsHarness.WalletBalance(node) - walletBefore).Satoshi
                                 == (toRemote.Value - fee).Satoshi,
                              s_timeout, "the wallet credited with our channel balance", ct);
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channel.ChannelId, out var resolving));
        Assert.Equal(ChannelState.OnchainResolving, resolving!.State);
    }

    /// <summary>LND <c>CloseChannel { force = true }</c>; returns its commitment once it is in the mempool.</summary>
    private async Task<Transaction> ForceCloseAsync(LNDNodeConnection lnd, OutPoint fundingOutPoint,
                                                    CancellationToken ct)
    {
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint
            {
                FundingTxidStr = fundingOutPoint.Hash.ToString(),
                OutputIndex = fundingOutPoint.N
            },
            Force = true
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);

        var commitment = await Poll.ForAsync(async () => await _harness.FindMempoolSpenderAsync(fundingOutPoint, ct),
                                             s_timeout, "LND's commitment in the mempool", ct);
        Console.WriteLine($"{lnd.LocalAlias} force-closed with {commitment.GetHash()} before the restore");
        return commitment;
    }

    private static void AddBackupServices(IServiceCollection services) =>
        services.AddChannelBackupNodeServices(new ConfigurationBuilder().Build());

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>()
                          .HandleAsync(request, ct);
    }

    /// <summary>
    /// Opens a channel of <see cref="s_capacity"/> to <paramref name="lnd"/>, waits until both ends use it and checks
    /// its type on both ends.
    /// </summary>
    private async Task<OpenChannelClientSubscriptionResponse> OpenChannelAsync(
        NLightningTestNode node, LNDNodeConnection lnd, bool anchors, CancellationToken ct)
    {
        await _harness.EnsureLndWalletFundedAsync(lnd, ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await node.ConnectToAsync(lnd, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            FeeRatePerKw = AnchorsHarness.EstimateFeeRatePerKw
        }, ct);
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && theirs is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);

        Assert.Equal(anchors, AnchorsHarness.GetModel(node, channel.ChannelId).ChannelParams.OptionAnchorOutputs);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        Assert.Equal(anchors ? CommitmentType.Anchors : CommitmentType.StaticRemoteKey, lndChannel.CommitmentType);
        return channel;
    }
}