using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Fixtures;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Force closes and BOLT 5 on-chain resolution against ldk-server (NL-556), on anchors channels (both ends negotiate
/// <c>option_anchors</c>): (a) we force-close a channel we funded: LDK sees our commitment, sweeps its CSV-1
/// <c>to_remote</c>, and we sweep our <c>to_local</c> after LDK's <c>to_self_delay</c>; (b) LDK force-closes a channel it
/// funded: we classify its commitment and sweep our CSV-1 <c>to_remote</c>, LDK sweeps its <c>to_local</c> after ours;
/// (c) our HTLC to LDK's hold invoice is in flight when we force-close and LDK claims it on chain with the preimage
/// afterwards: our payment succeeds with the preimage read from the chain; (d) the same HTLC never claimed: our
/// HTLC-timeout (an anchors second-level transaction we fund with a wallet input) confirms after <c>cltv_expiry</c>
/// and the payment fails once it is reasonably deep.
/// </summary>
/// <remarks>
/// <para>Everything is observed from outside the resolvers: our <c>forceclosechannel</c>, <c>pendingsweeps</c>-level
/// rows (<c>OutputResolutions</c>), our wallet and payments, bitcoind, and LDK's <c>list-channels</c>,
/// <c>get-balances</c> and <c>list-payments</c>. LDK claims what it is owed through its own <c>OutputSweeper</c>
/// (spendable outputs once 6 blocks deep), which is why the LDK side is checked by its on-chain balance.</para>
/// <para>Each test builds its own node and channel. Run with <c>scripts/run-interop.sh ldk Release -class
/// NLightning.Integration.Tests.Docker.Interop.Ldk.LdkOnchainTests</c>.</para>
/// </remarks>
[Collection(LdkInteropCollection.Name)]
[Trait("Category", LdkInteropCollection.Category)]
public sealed class LdkOnchainTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 12 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time while waiting for a sweep or a claim.</summary>
    private const int MaxSweepBlocks = 40;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(500_000);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);

    private readonly LdkFixture _fixture;
    private readonly List<LdkChannelSession> _sessions = [];

    public LdkOnchainTests(LdkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var session in _sessions)
            {
                Console.WriteLine($"[ldk] channel at failure: {await session.DescribeAsync(CancellationToken.None)}");
                foreach (var row in await SafeRowsAsync(session))
                    Console.WriteLine($"[ldk] row {Describe(row)}");
                foreach (var line in session.Node.NodeLog.TakeLast(200))
                    Console.WriteLine(line);
            }

            Console.WriteLine($"[ldk] balances at failure: {await SafeBalancesAsync()}");
            await DockerDiagnostics.DumpContainerLogsAsync([LdkFixture.LdkContainerName], 400);
        }

        foreach (var session in _sessions)
            await session.DisposeAsync();
    }

    /// <summary>
    /// (a) A channel we fund (500k sat, 100k pushed), a payment each way, then our <c>forceclosechannel</c>: our
    /// commitment confirms, LDK drops the channel and sweeps its CSV-1 <c>to_remote</c> to its wallet, and after LDK's
    /// <c>to_self_delay</c> our <c>to_local</c> is swept to our wallet (our balance less the commitment and sweep fees).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelWeFunded_When_WeForceClose_Then_LdkSweepsItsOutputAndWeSweepOurToLocal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildOurFundedAsync(
                                         _fixture, "nltg-ldk-fc-we", s_capacity, LightningMoney.Satoshis(100_000),
                                         ct));
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(5_000), ct);
        var ours = await session.GetOurChannelAsync(ct);
        var model = AnchorsHarness.GetModel(session.Node, session.ChannelId);
        var toSelfDelay = model.ChannelParams.Remote.ToSelfDelay;
        var ldkBalanceSat = (long)ours.RemoteBalance.Satoshi;
        var ldkOnchainBefore = await TotalOnchainSatAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);

        // Act
        var forceClose = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                             session, new ForceCloseChannelClientRequest(session.ChannelId), ct);

        // Assert: our commitment broadcast and confirmed, recorded as our local commitment
        Console.WriteLine($"[ldk] forceclosechannel: {forceClose.Status} {forceClose.CommitmentTxId}");
        Assert.Equal("Broadcast", forceClose.Status);
        Assert.Equal(ChannelState.Failed, forceClose.State);
        Assert.NotNull(forceClose.CommitmentTxId);
        var commitmentTxId = new uint256((byte[])forceClose.CommitmentTxId.Value);
        await MineUntilConfirmedAsync(session, commitmentTxId, ct);
        var close = await AnchorsHarness.WaitForCloseAsync(session.Node, session.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment, close.Kind);
        Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));

        // LDK drops the channel and sweeps its to_remote (CSV 1) once its sweeper sees it deep enough
        await Poll.UntilAsync(async () => await session.Ldk.GetChannelAsync(session.ChannelIdHex, ct) is null,
                              s_timeout, "LDK no longer lists the channel", ct);
        await MineUntilAsync(session, async () => await TotalOnchainSatAsync(ct) >= ldkOnchainBefore
                                                                                   + ldkBalanceSat - 5_000,
                             "LDK's wallet holds its channel balance less the sweep fee", ct);
        Console.WriteLine($"[ldk] LDK on chain: {ldkOnchainBefore} -> {await TotalOnchainSatAsync(ct)} sat "
                        + $"(channel balance {ldkBalanceSat} sat)");

        // Our to_local waits for LDK's to_self_delay, then is swept to our wallet
        var toLocal = Assert.Single(await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId),
                                    r => r.Descriptor == OutputDescriptorKind.DelayedToLocal);
        Console.WriteLine($"[ldk] our to_local {Describe(toLocal)}; LDK's to_self_delay {toSelfDelay}");
        Assert.Null(toLocal.ResolvingTransactionId);
        await _fixture.MineAndWaitAsync(toSelfDelay - 1, [session.Node], ct);
        var sweepTxId = await MineUntilResolvingTxAsync(session, toLocal, ct);
        var sweep = await MineUntilConfirmedAsync(session, new uint256((byte[])sweepTxId), ct);
        var input = Assert.Single(sweep.Transaction.Inputs, i => i.PrevOut.Hash == commitmentTxId);
        Assert.Equal(toLocal.OutputIndex, input.PrevOut.N);
        Assert.Equal((uint)toSelfDelay, input.Sequence.Value);
        await Poll.UntilAsync(() => Task.FromResult((AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi
                                                 >= ours.LocalBalance.Satoshi - 20_000),
                              s_timeout, "our wallet holds our channel balance less the fees", ct);
        Console.WriteLine($"[ldk] our balance {ours.LocalBalance.Satoshi} sat, wallet "
                        + $"+{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");
    }

    /// <summary>
    /// (b) LDK funds a 500k sat channel to us and pays us 60k (we pay back 10k), then force-closes: we classify LDK's
    /// commitment as the remote commitment and sweep our CSV-1 <c>to_remote</c> to our wallet; LDK sweeps its
    /// <c>to_local</c> after our <c>to_self_delay</c>.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdkFundedChannel_When_LdkForceCloses_Then_WeSweepOurToRemoteAndLdkItsToLocal()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildLdkFundedAsync(_fixture, "nltg-ldk-fc-ldk", s_capacity,
                                                                           ct));
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(60_000), ct);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(10_000), ct);
        var ours = await session.GetOurChannelAsync(ct);
        var model = AnchorsHarness.GetModel(session.Node, session.ChannelId);
        var ourToSelfDelay = model.ChannelParams.Local.ToSelfDelay;
        var ldkOnchainBefore = await TotalOnchainSatAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);

        // Act
        var closed = await session.Ldk.ForceCloseChannelAsync(session.UserChannelId, session.Node.NodeIdHex, ct);
        Console.WriteLine($"[ldk] force-close-channel: {closed.ToJsonString()}");

        // Assert: LDK's commitment confirms and we record it as the remote commitment
        var close = await MineUntilAsync(session, async () =>
                                             await TryGetCloseAsync(session) is { } c ? c : null,
                                         "we see LDK's commitment confirmed", ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        var commitmentTxId = new uint256((byte[])close.CommitmentTransactionId);
        Console.WriteLine($"[ldk] LDK's commitment {commitmentTxId}");

        // Our to_remote (CSV 1, P2WSH on anchors channels) is swept to our wallet
        var toRemote = await AnchorsHarness.WaitForRowAsync(session.Node, session.ChannelId,
                                                            r => r.Descriptor == OutputDescriptorKind.PaymentToRemote,
                                                            "our to_remote row", ct);
        var sweepTxId = await MineUntilResolvingTxAsync(session, toRemote, ct);
        var sweep = await MineUntilConfirmedAsync(session, new uint256((byte[])sweepTxId), ct);
        var input = Assert.Single(sweep.Transaction.Inputs, i => i.PrevOut.Hash == commitmentTxId);
        Assert.Equal(1u, input.Sequence.Value);
        Assert.True(AnchorsHarness.IsAnchorsToRemoteSpend(input.WitScript), "not the CSV-1 to_remote spend");
        await Poll.UntilAsync(() => Task.FromResult((AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi
                                                 >= ours.LocalBalance.Satoshi - 5_000),
                              s_timeout, "our wallet holds our channel balance less the sweep fee", ct);
        Console.WriteLine($"[ldk] our balance {ours.LocalBalance.Satoshi} sat, wallet "
                        + $"+{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");

        // LDK's to_local waits for our to_self_delay, then LDK sweeps it
        var ldkBalanceSat = (long)ours.RemoteBalance.Satoshi;
        await _fixture.MineAndWaitAsync(ourToSelfDelay, [session.Node], ct);
        await MineUntilAsync(session, async () => await TotalOnchainSatAsync(ct) >= ldkOnchainBefore
                                                                                   + ldkBalanceSat - 20_000,
                             "LDK's wallet holds its to_local", ct);
        Console.WriteLine($"[ldk] LDK on chain: {ldkOnchainBefore} -> {await TotalOnchainSatAsync(ct)} sat "
                        + $"(channel balance {ldkBalanceSat} sat, our to_self_delay {ourToSelfDelay})");
    }

    /// <summary>
    /// (c) We pay LDK's hold invoice (30k sat); LDK holds the HTLC. We force-close, our commitment confirms, then LDK
    /// is told the preimage (<c>bolt11-claim-for-id</c>): LDK claims the HTLC output of our commitment with it, we read
    /// the preimage from the chain and our payment succeeds with it; LDK lists the payment as received.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurHtlcHeldByLdk_When_WeForceCloseAndLdkClaimsItOnChain_Then_OurPaymentSucceeds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildOurFundedAsync(_fixture, "nltg-ldk-fc-claim", s_capacity,
                                                                           null, ct));
        var (preimage, hashHex, ldkPaymentId, htlc) = await session.SendHeldHtlcAsync("nltg ldk on-chain claim", ct);

        // Act: we force-close; once our commitment confirms LDK learns the preimage
        var forceClose = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                             session, new ForceCloseChannelClientRequest(session.ChannelId), ct);
        Assert.NotNull(forceClose.CommitmentTxId);
        var commitmentTxId = new uint256((byte[])forceClose.CommitmentTxId.Value);
        await MineUntilConfirmedAsync(session, commitmentTxId, ct);
        Assert.Equal(ChannelCloseKind.LocalCommitment,
                     (await AnchorsHarness.WaitForCloseAsync(session.Node, session.ChannelId, ct)).Kind);
        var claim = await session.Ldk.Bolt11ClaimForIdAsync(ldkPaymentId, Convert.ToHexString(preimage), ct);
        Console.WriteLine($"[ldk] bolt11-claim-for-id after the close: {claim.ToJsonString()}");

        // Assert: LDK's preimage spend of our HTLC output confirms and our payment succeeds with that preimage
        var paymentHash = new Hash(SHA256.HashData(preimage));
        var payment = await MineUntilAsync(session, async () =>
                                               await session.Node.GetPaymentAsync(paymentHash, ct) is
                                               { Status: PaymentStatus.Succeeded } p
                                                   ? p
                                                   : null,
                                           "our payment succeeded from the on-chain preimage", ct);
        Assert.NotNull(payment.Preimage);
        Assert.Equal(preimage, (byte[])payment.Preimage.Value);
        var row = Assert.Single(await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId),
                                r => r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc);
        Console.WriteLine($"[ldk] our HTLC output {Describe(row)}");
        Assert.Equal(htlc.Id, row.HtlcId);
        var spender = await FindChainSpenderAsync(new OutPoint(commitmentTxId, row.OutputIndex), ct);
        Assert.NotNull(spender);
        Assert.Contains(spender.Inputs, i => i.WitScript.Pushes.Any(p => p.SequenceEqual(preimage)));
        await Poll.UntilAsync(async () => string.Equals(LdkClient.StatusOf(await session.Ldk.FindPaymentByHashAsync(
                                                            hashHex, ct)), "SUCCEEDED",
                                                        StringComparison.OrdinalIgnoreCase),
                              s_timeout, "LDK lists the payment as received", ct);
    }

    /// <summary>
    /// (d) The same held HTLC, never claimed: after our force close nothing is spent before <c>cltv_expiry</c>; then our
    /// HTLC-timeout transaction (LDK's <c>SIGHASH_SINGLE|ANYONECANPAY</c> signature, our wallet input for the fee)
    /// confirms, and our payment fails once the timeout is reasonably deep.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurHtlcHeldByLdk_When_WeForceCloseAndItExpires_Then_OurTimeoutConfirmsAndPaymentFails()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildOurFundedAsync(_fixture, "nltg-ldk-fc-timeout",
                                                                           s_capacity, null, ct));
        var (preimage, hashHex, _, htlc) = await session.SendHeldHtlcAsync("nltg ldk on-chain timeout", ct);
        var paymentHash = new Hash(SHA256.HashData(preimage));

        // Act
        var forceClose = await HandleAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                             session, new ForceCloseChannelClientRequest(session.ChannelId), ct);
        Assert.NotNull(forceClose.CommitmentTxId);
        var commitmentTxId = new uint256((byte[])forceClose.CommitmentTxId.Value);
        await MineUntilConfirmedAsync(session, commitmentTxId, ct);
        var row = await AnchorsHarness.WaitForRowAsync(session.Node, session.ChannelId,
                                                       r => r.Descriptor == OutputDescriptorKind.LocalOfferedHtlc,
                                                       "our HTLC output row", ct);
        Assert.Equal(htlc.Id, row.HtlcId);

        // Assert: nothing before cltv_expiry, our HTLC-timeout from it on
        var tip = await _fixture.Chain.GetTipAsync(ct);
        if (htlc.CltvExpiry - 1 > tip)
            await _fixture.MineAndWaitAsync((int)(htlc.CltvExpiry - 1 - tip), [session.Node], ct);
        Assert.Null(await FindChainSpenderAsync(new OutPoint(commitmentTxId, row.OutputIndex), ct));
        Assert.Equal(PaymentStatus.InFlight, (await session.Node.GetPaymentAsync(paymentHash, ct))?.Status);
        var timeoutTxId = await MineUntilResolvingTxAsync(session, row, ct);
        var timeout = (await MineUntilConfirmedAsync(session, new uint256((byte[])timeoutTxId), ct)).Transaction;
        var input = Assert.Single(timeout.Inputs, i => i.PrevOut == new OutPoint(commitmentTxId, row.OutputIndex));
        Assert.Equal(htlc.CltvExpiry, timeout.LockTime.Value);
        Assert.True(timeout.Inputs.Count > 1, "the anchors HTLC-timeout has no fee input of ours");
        Assert.Equal(AnchorsHarness.SigHashSingleAnyoneCanPay, AnchorsHarness.SigHashOf(input.WitScript[1]));
        Console.WriteLine($"[ldk] our HTLC-timeout {timeout.GetHash()} at locktime {timeout.LockTime}, "
                        + $"{timeout.Inputs.Count} inputs");

        await _fixture.MineAndWaitAsync((int)AnchorsHarness.ReasonableDepth, [session.Node], ct);
        await Poll.UntilAsync(async () => (await session.Node.GetPaymentAsync(paymentHash, ct))?.Status
                                       == PaymentStatus.Failed,
                              s_timeout, "our payment failed after the on-chain timeout", ct);
        Console.WriteLine($"[ldk] LDK's payment at the end: "
                        + $"{(await session.Ldk.FindPaymentByHashAsync(hashHex, ct))?.ToJsonString()}");
    }

    private async Task<LdkChannelSession> OwnAsync(Task<LdkChannelSession> build)
    {
        var session = await build;
        _sessions.Add(session);
        return session;
    }

    private async Task<long> TotalOnchainSatAsync(CancellationToken ct) =>
        (await _fixture.Ldk.GetBalancesAsync(ct))["total_onchain_balance_sats"]?.GetValue<long>() ?? 0;

    private async Task<string> SafeBalancesAsync()
    {
        try
        {
            return (await _fixture.Ldk.GetBalancesAsync(CancellationToken.None)).ToJsonString();
        }
        catch (Exception e)
        {
            return $"unavailable: {e.Message}";
        }
    }

    private static async Task<IReadOnlyList<OutputResolutionModel>> SafeRowsAsync(LdkChannelSession session)
    {
        try
        {
            return session.Node.IsRunning
                       ? await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId)
                       : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<ChannelCloseModel?> TryGetCloseAsync(LdkChannelSession session)
    {
        using var scope = session.Node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>()
                          .OnchainResolutionDbRepository.GetCloseAsync(session.ChannelId);
    }

    private static string Describe(OutputResolutionModel row) =>
        $"{new uint256((byte[])row.TransactionId)}:{row.OutputIndex} {row.Descriptor} {row.State} "
      + $"htlc={row.HtlcId?.ToString() ?? "-"} wait={row.WaitUntilHeight?.ToString() ?? "-"} "
      + $"resolving={(row.ResolvingTransactionId is { } r ? new uint256((byte[])r).ToString() : "-")}";

    /// <summary>Mines one block at a time (up to <see cref="MaxSweepBlocks"/>) until <paramref name="probe"/> holds.</summary>
    private async Task<T> MineUntilAsync<T>(LdkChannelSession session, Func<Task<T?>> probe, string what,
                                            CancellationToken ct) where T : class
    {
        for (var i = 0; i < MaxSweepBlocks; i++)
        {
            if (await probe() is { } done)
                return done;

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return await Poll.ForAsync(probe, s_timeout, what, ct);
    }

    private Task MineUntilAsync(LdkChannelSession session, Func<Task<bool>> probe, string what, CancellationToken ct) =>
        MineUntilAsync(session, async () => await probe() ? new object() : null, what, ct);

    private async Task<TxId> MineUntilResolvingTxAsync(LdkChannelSession session, OutputResolutionModel row,
                                                       CancellationToken ct)
    {
        var found = await MineUntilAsync<object>(session, async () =>
        {
            var current = (await AnchorsHarness.GetRowsAsync(session.Node, session.ChannelId))
               .FirstOrDefault(r => r.TransactionId == row.TransactionId && r.OutputIndex == row.OutputIndex);
            return (object?)current?.ResolvingTransactionId;
        }, $"a resolving transaction for {row.Descriptor}", ct);
        return (TxId)found;
    }

    private async Task<NBitcoin.RPC.RawTransactionInfo> MineUntilConfirmedAsync(LdkChannelSession session,
                                                                                uint256 txId, CancellationToken ct) =>
        await MineUntilAsync(session, async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(txId, ct);
                return info.Confirmations >= 1 ? info : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return null; // not broadcast yet
            }
        }, $"transaction {txId} confirmed", ct);

    /// <summary>
    /// The confirmed transaction that spends <paramref name="outPoint"/>, searched in the blocks since its parent
    /// confirmed (the regtest chain is short), or null.
    /// </summary>
    private async Task<Transaction?> FindChainSpenderAsync(OutPoint outPoint, CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var parent = await rpc.GetRawTransactionInfoAsync(outPoint.Hash, ct);
        var tip = await rpc.GetBlockCountAsync(ct);
        for (var height = tip - (int)parent.Confirmations + 1; height <= tip; height++)
        {
            var block = await rpc.GetBlockAsync(height, ct);
            var spender = block.Transactions.FirstOrDefault(t => t.Inputs.Any(i => i.PrevOut == outPoint));
            if (spender is not null)
                return spender;
        }

        return null;
    }

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(LdkChannelSession session, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }
}