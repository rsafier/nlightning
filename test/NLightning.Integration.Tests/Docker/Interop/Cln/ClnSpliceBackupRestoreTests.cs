using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Fixtures;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Static channel backups across a splice (wave sp2 lane SP2-E, NL-478), against Core Lightning v26.06.8, which
/// splices. We fund a channel to CLN, export a backup, splice in 100,000 sat (our funding key rotates to index 1) and
/// wait until both ends locked it; the backup file the monitor keeps (<c>Node:Backup:FilePath</c>) is rewritten with
/// the splice's outpoint and our key index. Then the database is lost, the node restarts on the same key file and
/// <c>restorechanbackup</c> runs with the backup taken <b>before</b> the splice (the restore follows the splice
/// transaction to the new funding output) or <b>after</b> it (the backup names the splice): either way the recovery
/// channel is at the splice's funding with our rotated key, CLN force-closes on our data-loss
/// <c>channel_reestablish</c> and error, its commitment spends the splice's output, we record it as a commitment we
/// cannot rebuild and sweep our <c>to_remote</c> to the new wallet.
/// </summary>
/// <remarks>Written by lane SP2-E; the integrator runs it after the SP2 merge (it needs the SP1 splice and the splice
/// lock). From the host process: <c>NLightning.Integration.Tests -class
/// NLightning.Integration.Tests.Docker.Interop.Cln.ClnSpliceBackupRestoreTests</c>.</remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnSpliceBackupRestoreTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 15 * 60 * 1_000;
    private const int MaxBlocks = 12;
    private const uint OurFeeRatePerKw = 2_500;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(180);

    private readonly ClnFixture _fixture;
    private readonly string _backupDirectory = Path.Combine(Path.GetTempPath(), $"nltg-scb-{Guid.NewGuid():N}");
    private ClnChannelSession? _session;

    public ClnSpliceBackupRestoreTests(ClnFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            if (TestDiagnostics.CurrentTestFailed)
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await _session.DisposeAsync();
        }

        if (Directory.Exists(_backupDirectory))
            Directory.Delete(_backupDirectory, true);
    }

    /// <summary>The backup was taken before the splice: the restore follows the splice transaction.</summary>
    [Fact(Timeout = TestTimeoutMs)]
    public Task Given_ABackupFromBeforeTheSplice_When_Restored_Then_TheSpliceIsFollowedAndOurToRemoteIsSwept() =>
        RunAsync("nltg-scb-splice-pre", fromBeforeTheSplice: true);

    /// <summary>The backup file rewritten after the splice lock: it names the splice and our key index.</summary>
    [Fact(Timeout = TestTimeoutMs)]
    public Task Given_TheBackupRewrittenAfterTheSpliceLock_When_Restored_Then_ClnClosesOnTheSpliceAndOurToRemoteIsSwept() =>
        RunAsync("nltg-scb-splice-post", fromBeforeTheSplice: false);

    private async Task RunAsync(string nodeName, bool fromBeforeTheSplice)
    {
        // Arrange: a channel we fund to CLN, the backup file kept by the monitor, a backup before the splice
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_backupDirectory);
        var backupFile = Path.Combine(_backupDirectory, "channel.backup");
        var session = await BuildAsync(nodeName, backupFile, ct);
        var node = session.Node;
        StartBackupMonitor(node);
        var before = await HandleAsync<ExportChanBackupClientRequest, ExportChanBackupClientResponse>(
                         node, new ExportChanBackupClientRequest(), ct);
        var ourChannel = await session.GetOurChannelAsync(ct);
        var originalFunding = new uint256((byte[])ourChannel.FundingTxId!.Value);
        var originalKey = AnchorsHarness.GetModel(node, session.ChannelId).LocalFundingPubKey;

        // Act 1: we splice in 100,000 sat and both ends lock it
        var splice = await SpliceInAsync(session, 100_000, ct);
        Assert.Equal(SpliceNegotiationState.Signed, splice.State);
        var spliceTxId = new uint256((byte[])splice.SpliceTxId!.Value);
        await MineUntilLockedAsync(session, spliceTxId, ct);
        var spliced = AnchorsHarness.GetModel(node, session.ChannelId);
        Assert.NotEqual(originalKey, spliced.LocalFundingPubKey);
        var spliceOutPoint = new OutPoint(spliceTxId, spliced.FundingOutput!.Index!.Value);

        // Assert 1: the file was rewritten after the lock with the splice's funding and our key index
        var backupService = node.Services.GetRequiredService<IChannelBackupService>();
        var fromFile = await Poll.ForAsync(() =>
        {
            if (!File.Exists(backupFile))
                return Task.FromResult<ChannelBackupEntryView?>(null);

            var entry = backupService.Decrypt(File.ReadAllBytes(backupFile)).Channels
                                     .Single(c => c.ChannelId == session.ChannelId);
            return Task.FromResult(new uint256((byte[])entry.FundingTxId) == spliceTxId
                                       ? new ChannelBackupEntryView(entry.LocalFundingKeyIndex,
                                                                    entry.LocalFundingPubKey.ToString(),
                                                                    entry.FundingOutputIndex)
                                       : null);
        }, s_timeout, "the backup file rewritten with the splice", ct);
        Assert.Equal(1u, fromFile.LocalFundingKeyIndex);
        Assert.Equal(spliced.LocalFundingPubKey.ToString(), fromFile.LocalFundingPubKey);
        Assert.Equal((ushort)spliceOutPoint.N, fromFile.FundingOutputIndex);
        var preSplice = backupService.Decrypt(before.Backup).Channels.Single();
        Assert.Equal(originalFunding, new uint256((byte[])preSplice.FundingTxId));
        Assert.Equal(0u, preSplice.LocalFundingKeyIndex);

        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ourBalanceSat = (await session.GetOurChannelAsync(ct)).LocalBalance.Satoshi;
        var backup = fromBeforeTheSplice ? before.Backup : await File.ReadAllBytesAsync(backupFile, ct);

        // Act 2: the database is lost; the node restarts on the same key file and restores the chosen backup
        await session.StopNodeAsync();
        SqliteConnection.ClearAllPools();
        node.DeleteFiles();
        await session.StartNodeAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(node);
        var restore = await HandleAsync<RestoreChanBackupClientRequest, RestoreChanBackupClientResponse>(
                          node, new RestoreChanBackupClientRequest { Backup = backup }, ct);

        // Assert 2: the recovery channel is at the splice's funding with our rotated key
        var restored = Assert.Single(restore.Channels);
        Console.WriteLine($"Restore: {restored.Outcome}: {restored.Detail}");
        Assert.Equal("Restore", restored.Outcome);
        if (fromBeforeTheSplice)
            Assert.StartsWith("FundingSpliced", restored.Detail);
        var recovery = AnchorsHarness.GetModel(node, session.ChannelId);
        Assert.True(RecoveryChannels.IsRecoveryChannel(recovery) || recovery.State
                                                                   == Domain.Channels.Enums.ChannelState.OnchainResolving);
        Assert.Equal(spliceTxId, new uint256((byte[])recovery.FundingOutput!.TransactionId!.Value));
        Assert.Equal(spliced.LocalFundingPubKey, recovery.LocalFundingPubKey);

        // CLN force-closes on our data-loss reestablish: its commitment spends the splice's funding output
        var clnCommitment = await Poll.ForAsync(async () => await FindMempoolSpenderAsync(spliceOutPoint, ct),
                                                s_timeout, "CLN's commitment on the splice in the mempool", ct);
        Console.WriteLine($"CLN force-closed with {clnCommitment.GetHash()}");
        await MineUntilConfirmedAsync(node, clnCommitment.GetHash(), ct);

        // We recorded it as a commitment we cannot rebuild and sweep our to_remote (our whole balance)
        var close = await AnchorsHarness.WaitForCloseAsync(node, session.ChannelId, ct);
        TxId commitmentTxId = clnCommitment.GetHash().ToBytes();
        Assert.Equal(commitmentTxId, close.CommitmentTransactionId);
        Assert.Equal(ChannelCloseKind.FutureCommitment, close.Kind);
        var toRemoteRow = await AnchorsHarness.WaitForRowAsync(
                              node, session.ChannelId,
                              r => r.TransactionId == commitmentTxId
                                && r.Descriptor == OutputDescriptorKind.PaymentToRemote,
                              "our to_remote row", ct);
        var toRemote = clnCommitment.Outputs[(int)toRemoteRow.OutputIndex];

        // We funded the channel: the commitment fee and both anchors come out of our balance
        var commitmentFee = spliced.FundingOutput.Amount.Satoshi - clnCommitment.TotalOut.Satoshi;
        var anchors = spliced.ChannelParams.OptionAnchorOutputs ? 2 * 330 : 0;
        Console.WriteLine($"to_remote {toRemote.Value}, our balance {ourBalanceSat} sat, commitment fee "
                        + $"{commitmentFee} sat, anchors {anchors} sat");
        Assert.Equal(ourBalanceSat - commitmentFee - anchors, toRemote.Value.Satoshi);
        var sweepTxId = await MineUntilResolvingTxAsync(node, commitmentTxId, toRemoteRow.OutputIndex, ct);
        await MineUntilConfirmedAsync(node, new uint256((byte[])sweepTxId), ct);
        await Poll.UntilAsync(() => AnchorsHarness.WalletBalance(node).Satoshi - walletBefore.Satoshi
                                 > toRemote.Value.Satoshi * 99 / 100,
                              s_timeout, "the wallet credited with our channel balance", ct);
    }

    private async Task<ClnChannelSession> BuildAsync(string nodeName, string backupFile, CancellationToken ct)
    {
        _session = await ClnChannelSession.BuildOurFundedAsync(
                       _fixture, nodeName, s_capacity, s_push, ct,
                       node =>
                       {
                           node.ExtraConfiguration["Node:Backup:FilePath"] = backupFile;
                           node.ConfigureServices = services =>
                           {
                               services.PostConfigure<NodeOptions>(o =>
                               {
                                   o.Features.AllowExperimentalFeatures = true;
                                   o.Features.OptionQuiesce = FeatureSupport.Optional;
                                   o.Features.OptionSplice = FeatureSupport.Optional;
                               });
                               services.AddSpliceIpcServices();
                               services.PostConfigure<ChannelBackupOptions>(o =>
                               {
                                   o.FilePath = backupFile;
                                   o.WriteDelay = TimeSpan.FromMilliseconds(200);
                               });
                           };
                       });
        return _session;
    }

    /// <summary>The monitor the daemon's hosted service starts (the test node does not).</summary>
    private static void StartBackupMonitor(NLightningTestNode node) =>
        node.Services.GetRequiredService<ChannelBackupMonitor>().Start();

    private static async Task<SpliceClientResponse> SpliceInAsync(ClnChannelSession session, ulong amountSat,
                                                                  CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceInClientRequest(session.ChannelId, amountSat)
        {
            FeeRatePerKw = OurFeeRatePerKw
        }, ct);
        Console.WriteLine($"[nltg] splicein: {response.State}, txid {response.SpliceTxId}, reason "
                        + response.FailureReason);
        return response;
    }

    private async Task MineUntilLockedAsync(ClnChannelSession session, uint256 spliceTxId, CancellationToken ct)
    {
        for (var block = 1; ; block++)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var cln = await session.GetClnChannelAsync(ct);
            var ours = await session.GetOurChannelAsync(ct);
            if (cln["state"]?.GetValue<string>() == "CHANNELD_NORMAL"
             && cln["funding_txid"]?.GetValue<string>() == spliceTxId.ToString()
             && ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId)
                return;

            Assert.True(block < MaxBlocks, $"the splice was not locked by both ends in {MaxBlocks} blocks");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<Transaction?> FindMempoolSpenderAsync(OutPoint outPoint, CancellationToken ct)
    {
        foreach (var txId in await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct))
        {
            try
            {
                var tx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txId, true, ct);
                if (tx.Inputs.Any(i => i.PrevOut == outPoint))
                    return tx;
            }
            catch (Exception)
            {
                // Left the mempool meanwhile
            }
        }

        return null;
    }

    private async Task MineUntilConfirmedAsync(NLightningTestNode node, uint256 txId, CancellationToken ct)
    {
        for (var block = 0; block < MaxBlocks; block++)
        {
            await _fixture.MineAndWaitAsync(1, [node], ct);
            var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(txId, ct);
            if (info.Confirmations >= 1)
                return;
        }

        Assert.Fail($"{txId} did not confirm in {MaxBlocks} blocks");
    }

    /// <summary>Mines one block at a time until our node records the transaction that resolves the output.</summary>
    private async Task<TxId> MineUntilResolvingTxAsync(NLightningTestNode node, TxId commitmentTxId, uint vout,
                                                       CancellationToken ct)
    {
        for (var block = 0; block < MaxBlocks; block++)
        {
            var rows = await AnchorsHarness.GetRowsAsync(node, _session!.ChannelId);
            if (rows.FirstOrDefault(r => r.TransactionId == commitmentTxId && r.OutputIndex == vout)
                    ?.ResolvingTransactionId is { } resolving)
                return resolving;

            await _fixture.MineAndWaitAsync(1, [node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        Assert.Fail($"no resolving transaction for {commitmentTxId}:{vout} in {MaxBlocks} blocks");
        return default;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>()
                          .HandleAsync(request, ct);
    }

    private sealed record ChannelBackupEntryView(uint LocalFundingKeyIndex, string LocalFundingPubKey,
                                                 ushort FundingOutputIndex);
}