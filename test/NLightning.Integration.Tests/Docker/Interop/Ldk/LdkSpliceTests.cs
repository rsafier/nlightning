using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Abcd;
using Application.Channels.Splicing;
using Cln;
using Daemon.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Constants;
using Fixtures;
using Utils;
using SpliceWireRecorder = Cln.ClnSpliceTests.SpliceWireRecorder;

/// <summary>
/// Splicing and quiescence against ldk-server <c>dc02b76c</c> (LDK Node <c>f375e4d5</c> over rust-lightning
/// <c>697a239f</c>, 0.3.0-rc1; NL-556), with our default features (<c>option_splice</c> and <c>option_quiesce</c>
/// Optional since D13): (a) we splice in, (b) we splice out to an address, (c) LDK splices in, (d) LDK splices out to an
/// address, (e) LDK restarts while our splice is pending. Every splice is checked on the wire (the quiescence initiator's
/// <c>stfu</c> first, <c>splice_init</c>/<c>splice_ack</c>, a <c>commitment_signed</c> each way for the new funding
/// before any <c>tx_signatures</c>, both <c>tx_signatures</c> with the <c>shared_input_signature</c>), on chain (the
/// splice transaction spends the old funding output and creates the new one of the expected capacity) and once locked
/// (both <c>splice_locked</c>, LDK and we list the new funding outpoint, capacity and short channel id), with payments
/// both ways while it is pending and after the lock.
/// </summary>
/// <remarks>
/// <para><b>What ldk-server offers (checked on the pinned commit, 2026-09-29):</b> <c>splice-in &lt;user_channel_id&gt;
/// &lt;peer&gt; &lt;amount|all&gt;</c>, <c>splice-out &lt;user_channel_id&gt; &lt;peer&gt; &lt;amount&gt; [--address]</c>
/// (without an address to LDK's own wallet) and <c>bump-channel-funding-fee</c> (RBF of a pending splice at a feerate
/// LDK picks). Each answers once LDK handed its contribution to its channel manager; the negotiation runs after that.
/// LDK Node sets <c>reject_inbound_splices = false</c>, so it accepts our splices; no configuration is needed on
/// either side. LDK splices at its <c>ChannelFunding</c> estimate (bitcoind's <c>estimatesmartfee</c>, 1,000 sat/kw
/// fallback on the idle regtest) and refuses to pay more than 1.5 times it. rust-lightning implements the final BOLT 2
/// splice messages we do: <c>splice_init</c> 80, <c>splice_ack</c> 81, <c>splice_locked</c> 77, <c>start_batch</c>
/// 127, <c>channel_reestablish</c> TLVs 1 (<c>next_funding</c>) and 5 (<c>my_current_funding_locked</c>), feature
/// bits 34/35 and 62/63. ldk-server logs <c>SPLICE_NEGOTIATED</c>/<c>SPLICE_NEGOTIATION_FAILED</c>; its
/// <c>list-channels</c> has no pending-splice field, so a pending splice is seen on our side and in bitcoind's
/// mempool, and LDK's <c>funding_txo</c> moves at the lock.</para>
/// <para><b>What LDK did in the runs (lane ldksplice):</b> its splice-in contribution carries the surplus of its coin
/// selection (+100,002..+100,006 sat for 100,000), its splice-out contribution is -(amount + fee) (-50,183 for 50,000
/// at 253 sat/kw); it budgets the shared input at 164 + 219 wu, which our receiver refused at 2,488 sat/kw until
/// NL-558; its RBF takes the BOLT 2 minimum (2,490 -> 2,593 sat/kw); it accepts our RBF with 0; it sends its
/// <c>splice_locked</c> at the channel's <c>minimum_depth</c> (6 on a channel we fund); every <c>commitment_signed</c>
/// while a splice is pending comes in a <c>start_batch</c>; it warns about our 65,531-byte <c>peer_storage</c>
/// (NL-559).</para>
/// <para>Each test builds its own node and channel and records our channel traffic both ways with
/// <see cref="SpliceWireRecorder"/> (the recorder of the CLN proofs). Run with <c>scripts/run-interop.sh ldk Release
/// -class NLightning.Integration.Tests.Docker.Interop.Ldk.LdkSpliceTests</c>.</para>
/// </remarks>
[Collection(LdkInteropCollection.Name)]
[Trait("Category", LdkInteropCollection.Category)]
public sealed class LdkSpliceTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 12 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time until both ends locked the splice.</summary>
    private const int MaxLockBlocks = 12;

    /// <summary>The feerate of our splices: the test node's 10 sat/vB estimate.</summary>
    private const uint OurFeeRatePerKw = 2_500;

    /// <summary>The floor feerate of our first attempt in the RBF proof (f).</summary>
    private const uint LowFeeRatePerKw = 253;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(400_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Our <c>Splice:MinRbfInterval</c> in the RBF proofs: the wall-clock override of the one-block rule (NL-520), since
    /// mining a block could confirm the first attempt.
    /// </summary>
    private static readonly TimeSpan s_minRbfInterval = TimeSpan.FromSeconds(2);

    private readonly LdkFixture _fixture;
    private LdkChannelSession? _session;
    private SpliceWireRecorder? _wire;

    public LdkSpliceTests(LdkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_wire is not null)
            Console.WriteLine($"[wire] {_wire.Describe()}");

        if (_session is not null)
        {
            if (DockerDiagnostics.CurrentTestFailed)
            {
                Console.WriteLine($"[ldk] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
                await DockerDiagnostics.DumpContainerLogsAsync([LdkFixture.LdkContainerName], 400);
            }

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// (a) We splice in 100,000 sat (<c>splicein</c>, 2,500 sat/kw) on a channel we funded: we are the quiescence
    /// initiator, LDK acks with 0, the transaction pays our feerate; a payment each way while pending; both lock it,
    /// LDK lists the new funding, capacity and short channel id; payments both ways after, our balance grew by 100,000.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeSpliceIn_When_TheSpliceLocks_Then_LdkAgreesAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-ldk-splice-a", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        var response = await SpliceInAsync(session, 100_000, ct);

        // Assert: we started it and LDK acked with 0
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.Null(response.FailureReason);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        var expectedCapacity = before.CapacitySat + 100_000;
        Assert.Equal(expectedCapacity, response.NewCapacitySat);
        var init = wire.First(inbound: false, MessageTypes.SpliceInit, from);
        var ack = wire.First(inbound: true, MessageTypes.SpliceAck, from);
        Assert.Equal(100_000, init.SpliceContributionSatoshis);
        Assert.Equal(OurFeeRatePerKw, init.SpliceFeeratePerKw);
        Assert.Equal(0, ack.SpliceContributionSatoshis);
        AssertQuiescence(wire, from, weInitiated: true, init.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, OurFeeRatePerKw, ct);

        // ...payments both ways while pending, then the lock and payments after
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (b) We splice 50,000 sat out to an address of bitcoind's wallet (<c>spliceout --address</c>): the transaction
    /// pays exactly 50,000 sat there and funds the channel with the rest; our <c>splice_init</c> carries
    /// -(50,000 + fee), LDK acks with 0; payments both ways while pending; after the lock the capacity and our balance
    /// dropped by amount + fee on both ends, and payments flow both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeSpliceOutToAnAddress_When_TheSpliceLocks_Then_TheAddressIsPaidAndCapacityDrops()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-ldk-splice-b", ct);
        var before = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        var from = wire.CurrentSequence;

        // Act
        var response = await SpliceOutAsync(session, 50_000, address.ToString(), ct);

        // Assert
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.Null(response.FailureReason);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        var init = wire.First(inbound: false, MessageTypes.SpliceInit, from);
        Assert.Equal(0, wire.First(inbound: true, MessageTypes.SpliceAck, from).SpliceContributionSatoshis);
        AssertQuiescence(wire, from, weInitiated: true, init.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        var fee = await GetFeeAsync(spliceTxId, ct);
        Assert.Equal(-(long)(50_000 + fee), init.SpliceContributionSatoshis);
        var expectedCapacity = before.CapacitySat - 50_000 - fee;
        Assert.Equal(expectedCapacity, response.NewCapacitySat);
        var tx = await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, OurFeeRatePerKw, ct);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey && o.Value == Money.Satoshis(50_000));

        // ...payments while pending; lock: confirmed, our balance paid amount + fee, payments after
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        var info = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(spliceTxId, ct);
        Assert.True(info.Confirmations >= 1, "the splice transaction is not confirmed");
        Assert.Equal(before.LocalBalanceMsat - (long)(50_000 + fee) * 1_000 - 20_000_000 + 10_000_000,
                     after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (c) LDK splices 100,000 sat in (<c>splice-in</c>) on a channel LDK opened to us: LDK is the quiescence initiator
    /// and sends <c>splice_init</c> with at least +100,000 (plus any sub-dust leftover of its coin selection), we accept
    /// with 0; the capacity grows by LDK's contribution and our balance moves only by the payments (both ways while
    /// pending and after the lock).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdkSplicesIn_When_TheSpliceLocks_Then_WeAcceptAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildLdkFundedAsync("nltg-ldk-splice-c", ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(50_000), ct);
        await _fixture.FundLdkWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        await session.Ldk.SpliceInAsync(session.UserChannelId, session.Node.NodeIdHex, 100_000, ct);

        // Assert: LDK started it, we acked with 0
        var (init, spliceTxId, signaturesAt) = await AssertPeerSpliceAsync(session, wire, from, ct);
        // LDK adds what its coin selection leaves below the dust limit to its contribution instead of a change output
        // (100,002 sat for 100,000 in the first run); BOLT 2 only asks that the contribution be what it adds
        Assert.InRange(init.SpliceContributionSatoshis, 100_000, 100_000 + 330);
        var expectedCapacity = (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis);
        await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, null, ct);

        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (d) LDK splices 50,000 sat out to an address of bitcoind's wallet (<c>splice-out --address</c>): LDK's
    /// <c>splice_init</c> carries a negative contribution of at least the amount, we accept with 0; the transaction pays
    /// the address, the capacity drops by LDK's contribution and our balance moves only by the payments (both ways
    /// while pending and after the lock).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdkSplicesOutToAnAddress_When_TheSpliceLocks_Then_OurBalanceStaysAndTheAddressIsPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-ldk-splice-d", ct);
        var before = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        var from = wire.CurrentSequence;

        // Act
        await session.Ldk.SpliceOutAsync(session.UserChannelId, session.Node.NodeIdHex, 50_000, address.ToString(),
                                          ct);

        // Assert
        var (init, spliceTxId, signaturesAt) = await AssertPeerSpliceAsync(session, wire, from, ct);
        Assert.True(init.SpliceContributionSatoshis <= -50_000,
                    $"LDK's contribution {init.SpliceContributionSatoshis} is above -50,000");
        var expectedCapacity = (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis);
        var tx = await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, null, ct);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey && o.Value == Money.Satoshis(50_000));
        Console.WriteLine($"[proof] LDK's contribution {init.SpliceContributionSatoshis}, fee "
                        + $"{await GetFeeAsync(spliceTxId, ct)} sat");

        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, before.FundingTxId, spliceTxId);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (e) Our splice-in is signed and pending when LDK restarts: we reconnect, both send <c>channel_reestablish</c>,
    /// the channel is usable on the old and pending fundings (a payment each way), and the splice then locks on both
    /// ends with payments after.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingSplice_When_LdkRestarts_Then_ReestablishedAndTheSpliceLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-ldk-splice-e", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var response = await SpliceInAsync(session, 100_000, ct);
        Assert.Equal(SpliceNegotiationState.Signed, response.State);
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        var restartedAt = wire.CurrentSequence;

        // Act
        await _fixture.RestartLdkAsync(ct);

        // Assert: reestablished both ways, payments on the pending splice, then the lock
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish, restartedAt),
                            LdkChannelSession.UsableTimeout, "LDK's channel_reestablish", ct);
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish, restartedAt),
                            LdkChannelSession.UsableTimeout, "our channel_reestablish", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        Assert.False((await session.GetOurChannelAsync(ct)).DataLossDetected);
        Assert.Contains(spliceTxId, await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct));
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (f) Splice RBF, ours: we splice in 100,000 sat at 253 sat/kw, then <c>bumpsplice</c> at 1,250 sat/kw. We are the
    /// quiescence initiator of the RBF and send <c>tx_init_rbf</c> at 1,250; LDK answers <c>tx_ack_rbf</c>; the new
    /// attempt is signed both ways and replaces the first in bitcoind's mempool; payments while both attempts are
    /// pending go in batches of three; the bumped attempt locks on both ends; payments after.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingSplice_When_WeBumpIt_Then_LdkFollowsTheRbfAndTheBumpLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-ldk-splice-f", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var first = await SpliceInAsync(session, 100_000, ct, LowFeeRatePerKw);
        Assert.Equal(SpliceNegotiationState.Signed, first.State);
        Assert.NotNull(first.SpliceTxId);
        var firstTxId = new uint256((byte[])first.SpliceTxId.Value);
        await AssertSignedSpliceAsync(session, wire, from, firstTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        var bumped = await BumpAsync(session, OurFeeRatePerKw / 2, ct);

        // Assert: our tx_init_rbf at the new feerate, LDK acked, the bump replaced the first attempt
        Assert.True(bumped.State == SpliceNegotiationState.Signed, $"the bump is {bumped.State}: {bumped.FailureReason}");
        Assert.NotNull(bumped.SpliceTxId);
        var bumpTxId = new uint256((byte[])bumped.SpliceTxId.Value);
        Assert.NotEqual(firstTxId, bumpTxId);
        var initRbf = wire.First(inbound: false, MessageTypes.TxInitRbf, rbfFrom);
        var ackRbf = wire.First(inbound: true, MessageTypes.TxAckRbf, rbfFrom);
        Assert.Equal(OurFeeRatePerKw / 2, RbfFeerate(initRbf));
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        AssertQuiescence(wire, rbfFrom, weInitiated: true, initRbf.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, rbfFrom, bumpTxId, ct);
        await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, OurFeeRatePerKw / 2, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        Assert.True(await GetFeeAsync(bumpTxId, ct) > firstFee, "the bump pays no more than the first attempt");

        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (g) Splice RBF, LDK's: LDK splices in 100,000 sat, then <c>bump-channel-funding-fee</c> (LDK picks the BOLT 2
    /// minimum RBF feerate). We get LDK's <c>stfu</c> and <c>tx_init_rbf</c>, answer <c>tx_ack_rbf</c>; the new attempt
    /// is signed both ways and replaces the first; the bump locks on both ends with our balance moved only by the
    /// payments.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdksPendingSplice_When_LdkBumpsIt_Then_WeFollowTheRbfAndTheBumpLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-ldk-splice-g", ct);
        await _fixture.FundLdkWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        await session.Ldk.SpliceInAsync(session.UserChannelId, session.Node.NodeIdHex, 100_000, ct);
        var (init, firstTxId, _) = await AssertPeerSpliceAsync(session, wire, from, ct);
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await Task.Delay(s_minRbfInterval + TimeSpan.FromSeconds(1), ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        await session.Ldk.BumpChannelFundingFeeAsync(session.UserChannelId, session.Node.NodeIdHex, ct);

        // Assert
        var initRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxInitRbf, rbfFrom),
                                          s_stepTimeout, "LDK's tx_init_rbf", ct);
        var ackRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAckRbf, rbfFrom),
                                         s_stepTimeout, "our tx_ack_rbf", ct);
        Console.WriteLine($"[proof] LDK's tx_init_rbf: feerate {RbfFeerate(initRbf)} sat/kw (first attempt "
                        + $"{init.SpliceFeeratePerKw})");
        Assert.True(RbfFeerate(initRbf) > init.SpliceFeeratePerKw);
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        AssertQuiescence(wire, rbfFrom, weInitiated: false, initRbf.Sequence);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures,
                                                                      rbfFrom),
                                            s_stepTimeout, "LDK's tx_signatures for the bump", ct);
        var bumpTxId = theirSigs.TxSignaturesTxId;
        Assert.NotEqual(firstTxId, bumpTxId);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, rbfFrom, bumpTxId, ct);
        var bumpTx = await GetTransactionAsync(bumpTxId);
        var bumpFunding = Assert.Single(bumpTx.Outputs, o => o.ScriptPubKey.IsScriptType(ScriptType.P2WSH));
        var expectedCapacity = (ulong)bumpFunding.Value.Satoshi;
        Console.WriteLine($"[proof] LDK's bump {bumpTxId}: capacity {expectedCapacity} sat");
        Assert.InRange((long)expectedCapacity - (long)before.CapacitySat, 100_000, 100_000 + 330);
        await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, null, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        Assert.True(await GetFeeAsync(bumpTxId, ct) > firstFee, "LDK's bump pays no more than its first attempt");

        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);

        Task<Transaction> GetTransactionAsync(uint256 txId) => _fixture.Bitcoin.Rpc.GetRawTransactionAsync(txId, true, ct);
    }

    /// <summary>A channel we fund to LDK (1M sat, 400k pushed) with our traffic recorded.</summary>
    private async Task<(LdkChannelSession Session, SpliceWireRecorder Wire)> BuildOurFundedAsync(
        string nodeName, CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        _session = await LdkChannelSession.BuildOurFundedAsync(_fixture, nodeName, s_capacity, s_push, ct,
                                                               node => InstallRecorder(node, wire));
        AssertSpliceNegotiated(_session);
        return (_session, wire);
    }

    /// <summary>A channel LDK funds to us (1M sat) with our traffic recorded.</summary>
    private async Task<(LdkChannelSession Session, SpliceWireRecorder Wire)> BuildLdkFundedAsync(
        string nodeName, CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        _session = await LdkChannelSession.BuildLdkFundedAsync(_fixture, nodeName, s_capacity, ct,
                                                               configureNode: node => InstallRecorder(node, wire));
        AssertSpliceNegotiated(_session);
        return (_session, wire);
    }

    private static void InstallRecorder(NLightningTestNode node, SpliceWireRecorder wire) =>
        node.ConfigureServices = services =>
        {
            services.PostConfigure<SpliceOptions>(o => o.MinRbfInterval = s_minRbfInterval);
            wire.Install(services);
        };

    /// <summary>LDK advertises both 35 (<c>option_quiesce</c>) and 63 (<c>option_splice</c>) and so do we.</summary>
    private static void AssertSpliceNegotiated(LdkChannelSession session)
    {
        var peer = session.Node.PeerManager.GetPeer(session.LdkPubKey);
        Assert.NotNull(peer);
        foreach (var feature in new[] { Feature.OptionQuiesce, Feature.OptionSplice })
            Assert.True(peer.Features.IsFeatureSet(feature), $"LDK does not advertise {feature}");
    }

    private static async Task<SpliceClientResponse> SpliceInAsync(LdkChannelSession session, ulong amountSat,
                                                                  CancellationToken ct,
                                                                  uint feeRatePerKw = OurFeeRatePerKw)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceInClientRequest(session.ChannelId, amountSat)
        {
            FeeRatePerKw = feeRatePerKw
        }, ct);
        Console.WriteLine($"[nltg] splicein: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    private static async Task<SpliceClientResponse> SpliceOutAsync(LdkChannelSession session, ulong amountSat,
                                                                   string address, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<SpliceOutClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new SpliceOutClientRequest(session.ChannelId, amountSat)
        {
            Address = address,
            FeeRatePerKw = OurFeeRatePerKw
        }, ct);
        Console.WriteLine($"[nltg] spliceout: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary>Our <c>bumpsplice</c> through the daemon's client handler.</summary>
    private static async Task<SpliceClientResponse> BumpAsync(LdkChannelSession session, uint feeRatePerKw,
                                                              CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<BumpSpliceClientRequest, SpliceClientResponse>>();
        var response = await handler.HandleAsync(new BumpSpliceClientRequest(session.ChannelId, feeRatePerKw), ct);
        Console.WriteLine($"[nltg] bumpsplice at {feeRatePerKw} sat/kw: {response.State}, txid {response.SpliceTxId}, "
                        + $"capacity {response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary><c>tx_init_rbf</c>: <c>feerate</c> after <c>channel_id</c> and <c>locktime</c>.</summary>
    private static uint RbfFeerate(ClnSpliceTests.SpliceWireMessage message) =>
        BinaryPrimitives.ReadUInt32BigEndian(message.Wire.AsSpan(2 + 32 + 4, 4));

    private async Task AssertReplacedAsync(uint256 replacedTxId, uint256 bumpTxId, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Contains(bumpTxId) && !mempool.Contains(replacedTxId);
        }, s_stepTimeout, $"{bumpTxId} replaced {replacedTxId} in bitcoind's mempool", ct);

    /// <summary>
    /// The quiescence of a splice from <paramref name="from"/>: the initiator's <c>stfu</c> (initiator = 1) first, the
    /// other side's (initiator = 0) after it, both before <c>splice_init</c> at <paramref name="initSequence"/>.
    /// </summary>
    private static void AssertQuiescence(SpliceWireRecorder wire, long from, bool weInitiated, long initSequence)
    {
        var ours = wire.First(inbound: false, MessageTypes.Stfu, from);
        var theirs = wire.First(inbound: true, MessageTypes.Stfu, from);
        var (first, second) = weInitiated ? (ours, theirs) : (theirs, ours);
        Assert.True(first.StfuInitiator && !second.StfuInitiator,
                    $"{(weInitiated ? "we were" : "LDK was")} not the quiescence initiator");
        Assert.True(first.Sequence < second.Sequence && second.Sequence < initSequence,
                    "the stfu exchange did not come before splice_init");
    }

    /// <summary>
    /// A splice LDK started: LDK's <c>stfu</c> and <c>splice_init</c>, our <c>splice_ack</c> with 0, then the signed
    /// splice. Returns LDK's <c>splice_init</c>, the splice txid and the sequence the splice is pending from.
    /// </summary>
    private async Task<(ClnSpliceTests.SpliceWireMessage Init, uint256 SpliceTxId, long SignaturesAt)>
        AssertPeerSpliceAsync(LdkChannelSession session, SpliceWireRecorder wire, long from, CancellationToken ct)
    {
        var init = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceInit, from),
                                       s_stepTimeout, "LDK's splice_init", ct);
        var ack = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceAck, from),
                                      s_stepTimeout, "our splice_ack", ct);
        Console.WriteLine($"[proof] LDK's splice_init: contribution {init.SpliceContributionSatoshis} sat, feerate "
                        + $"{init.SpliceFeeratePerKw} sat/kw");
        Assert.Equal(0, ack.SpliceContributionSatoshis);
        Assert.True(init.Sequence < ack.Sequence);
        AssertQuiescence(wire, from, weInitiated: false, init.Sequence);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from),
                                            s_stepTimeout, "LDK's tx_signatures", ct);
        var spliceTxId = theirSigs.TxSignaturesTxId;
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        return (init, spliceTxId, signaturesAt);
    }

    /// <summary>
    /// The interactive part of a splice from <paramref name="from"/>: a <c>commitment_signed</c> each way for the new
    /// funding before any <c>tx_signatures</c>, no <c>revoke_and_ack</c> or <c>tx_abort</c> in between, both
    /// <c>tx_signatures</c> for the splice txid with a <c>shared_input_signature</c>; the transaction is in bitcoind's
    /// mempool and our negotiation is Signed. Returns the sequence after the last <c>tx_signatures</c>.
    /// </summary>
    private async Task<long> AssertSignedSpliceAsync(LdkChannelSession session, SpliceWireRecorder wire, long from,
                                                     uint256 spliceTxId, CancellationToken ct)
    {
        var ourSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxSignatures, from),
                                          s_stepTimeout, "our tx_signatures", ct);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from),
                                            s_stepTimeout, "LDK's tx_signatures", ct);
        foreach (var sigs in new[] { ourSigs, theirSigs })
        {
            Assert.Equal(spliceTxId, sigs.TxSignaturesTxId);
            Assert.True(sigs.HasSharedInputSignature,
                        $"{(sigs.Inbound ? "LDK's" : "our")} tx_signatures has no shared_input_signature");
        }

        var first = Math.Min(ourSigs.Sequence, theirSigs.Sequence);
        var ourCs = wire.First(inbound: false, MessageTypes.CommitmentSigned, from);
        var theirCs = wire.First(inbound: true, MessageTypes.CommitmentSigned, from);
        Assert.True(ourCs.Sequence < first && theirCs.Sequence < first,
                    "a tx_signatures went out before both splice commitment_signed");
        Assert.Equal(spliceTxId, ourCs.CommitmentFundingTxId);
        Assert.Equal(spliceTxId, theirCs.CommitmentFundingTxId);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Type == (ushort)MessageTypes.RevokeAndAck
                                                 && m.Sequence > from && m.Sequence < first);
        Assert.DoesNotContain(wire.Snapshot(), m => m.Type == (ushort)MessageTypes.TxAbort && m.Sequence > from);

        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(spliceTxId),
                              s_stepTimeout, "the splice transaction in bitcoind's mempool", ct);
        var negotiation = await Poll.ForAsync(() =>
        {
            var n = session.Node.Services.GetRequiredService<ISpliceService>().GetNegotiation(session.ChannelId);
            return Task.FromResult(n?.State == SpliceNegotiationState.Signed ? n : null);
        }, s_stepTimeout, "our splice negotiation Signed", ct);
        Assert.NotNull(negotiation.SpliceTxId);
        Assert.Equal(spliceTxId, new uint256((byte[])negotiation.SpliceTxId.Value));
        return Math.Max(ourSigs.Sequence, theirSigs.Sequence);
    }

    /// <summary>
    /// The splice transaction spends the previous funding output with a 2-of-2 witness and has exactly one P2WSH output
    /// of <paramref name="expectedCapacitySat"/> (the new funding); with <paramref name="feeRatePerKw"/> (ours) its fee
    /// is that feerate for its weight within one vbyte (plus 2 WU per ECDSA signature), else at least 253 sat/kw.
    /// </summary>
    private async Task<Transaction> AssertSpliceTransactionAsync(ChannelSnapshot before, uint256 spliceTxId,
                                                                 ulong expectedCapacitySat, uint? feeRatePerKw,
                                                                 CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var tx = await rpc.GetRawTransactionAsync(spliceTxId, true, ct);
        var shared = Assert.Single(tx.Inputs, i => i.PrevOut == new OutPoint(before.FundingTxId,
                                                                               before.FundingOutputIndex));
        Assert.Equal(4, shared.WitScript.PushCount);
        var funding = Assert.Single(tx.Outputs, o => o.ScriptPubKey.IsScriptType(ScriptType.P2WSH)
                                                  && o.Value == Money.Satoshis((long)expectedCapacitySat));

        var fee = await GetFeeAsync(spliceTxId, ct);
        var verbose = await rpc.SendCommandAsync("getrawtransaction", ct, spliceTxId.ToString(), true);
        var weight = (ulong)verbose.Result["weight"]!;
        var ecdsaSignatures = tx.Inputs.SelectMany(i => i.WitScript.Pushes)
                                .Count(p => p.Length is >= 9 and <= 73 && p[0] == 0x30);
        Console.WriteLine($"[proof] splice {spliceTxId}: funding output {tx.Outputs.IndexOf(funding)} of "
                        + $"{expectedCapacitySat} sat, {tx.Inputs.Count} inputs, {tx.Outputs.Count} outputs, fee {fee} "
                        + $"sat for {weight} WU ({fee * 1_000.0 / weight:F1} sat/kw)");
        var effective = feeRatePerKw ?? 253;
        var floor = effective * weight / 1_000;
        Assert.True(fee + 1 >= floor, $"the splice pays {fee} sat, less than {floor} sat at {effective} sat/kw");
        if (feeRatePerKw is { } ours)
        {
            var ceiling = (ours * (weight + 4 + 2 * (ulong)ecdsaSignatures) + 999) / 1_000;
            Assert.True(fee <= ceiling + 1, $"our splice pays {fee} sat, more than one vbyte above {floor} sat");
        }

        return tx;
    }

    private async Task<ulong> GetFeeAsync(uint256 txId, CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var tx = await rpc.GetRawTransactionAsync(txId, true, ct);
        var spent = Money.Zero;
        foreach (var input in tx.Inputs)
            spent += (await rpc.GetRawTransactionAsync(input.PrevOut.Hash, true, ct)).Outputs[input.PrevOut.N].Value;

        return (ulong)(spent - tx.TotalOut).Satoshi;
    }

    /// <summary>
    /// Mines one block at a time until both ends moved to the splice's funding: LDK's <c>funding_txo</c> and ours are
    /// the splice, both sent <c>splice_locked</c> for it, both list the new capacity and the same new short channel id,
    /// LDK's view of our balance matches ours, and the channel is usable again.
    /// </summary>
    private async Task<LockedChannel> MineUntilLockedAsync(LdkChannelSession session, SpliceWireRecorder wire,
                                                           ChannelSnapshot before, uint256 spliceTxId,
                                                           ulong expectedCapacitySat, CancellationToken ct)
    {
        for (var block = 1; ; block++)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var ldk = await session.GetLdkChannelAsync(ct);
            var ours = await session.GetOurChannelAsync(ct);
            Console.WriteLine($"[proof] +{block} block(s): ours {ours.Describe()} funding "
                            + $"{(ours.FundingTxId is { } f ? new uint256((byte[])f) : null)}; ldk "
                            + $"{LdkChannelSession.DescribeLdk(ldk)} funding {ldk["funding_txo"]?.ToJsonString()}");
            if (LdkFundingTxId(ldk) == spliceTxId.ToString()
             && ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId
             && wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked) is not null)
                break;

            Assert.True(block < MaxLockBlocks, $"the splice was not locked by both ends in {MaxLockBlocks} blocks");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var ourLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceLocked),
                                            s_stepTimeout, "our splice_locked", ct);
        var theirLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked),
                                              s_stepTimeout, "LDK's splice_locked", ct);
        Assert.Equal(spliceTxId, ourLocked.SpliceLockedTxId);
        Assert.Equal(spliceTxId, theirLocked.SpliceLockedTxId);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);

        var channel = await session.GetOurChannelAsync(ct);
        var ldkChannel = await session.GetLdkChannelAsync(ct);
        Console.WriteLine($"[proof] locked: ours {channel.Describe()}; ldk {LdkChannelSession.DescribeLdk(ldkChannel)}");
        Assert.Equal((long)expectedCapacitySat, channel.Capacity.Satoshi);
        Assert.Equal(expectedCapacitySat, ReadUInt64(ldkChannel["channel_value_sats"]));
        Assert.NotNull(channel.ShortChannelId);
        var ldkScid = ReadUInt64(ldkChannel["short_channel_id"]);
        Assert.NotEqual(before.ShortChannelId, ldkScid);
        Assert.Equal(ldkScid, channel.ShortChannelId.Value.ToUInt64());
        // LDK's outbound capacity is its balance less the reserve we asked of it and its anchors/commit fee share,
        // so compare the channel's total instead: both ends' balances add up to the new capacity
        Assert.Equal((long)expectedCapacitySat * 1_000,
                     (long)(channel.LocalBalance.MilliSatoshi + channel.RemoteBalance.MilliSatoshi));
        return new LockedChannel((long)channel.LocalBalance.MilliSatoshi,
                                 Math.Max(ourLocked.Sequence, theirLocked.Sequence));
    }

    private static string? LdkFundingTxId(JsonNode ldk) => ldk["funding_txo"]?["txid"]?.GetValue<string>();

    /// <summary>A u64 the CLI prints as a JSON number or string.</summary>
    private static ulong ReadUInt64(JsonNode? node) =>
        ulong.Parse(node?.ToString() ?? throw new InvalidOperationException("missing"),
                    System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// While the splice was pending (between <paramref name="from"/> and <paramref name="to"/>) every
    /// <c>commitment_signed</c> of each side came in a <c>start_batch</c> of two (the current funding and the splice)
    /// answered by one <c>revoke_and_ack</c>.
    /// </summary>
    private static void AssertBatchesWhilePending(SpliceWireRecorder wire, long from, long to, uint256 currentFunding,
                                                  uint256 spliceTxId) =>
        AssertBatchesWhilePending(wire, from, to, [currentFunding, spliceTxId]);

    /// <summary>
    /// As above with every active funding in <paramref name="fundings"/> (the current one and each pending attempt): a
    /// <c>start_batch</c> of that many <c>commitment_signed</c>, one per funding.
    /// </summary>
    private static void AssertBatchesWhilePending(SpliceWireRecorder wire, long from, long to,
                                                  IReadOnlyList<uint256> fundings)
    {
        var window = wire.Snapshot().Where(m => m.Sequence > from && m.Sequence < to && !m.IsPingOrPong).ToList();
        var counts = new int[2];
        foreach (var inbound in new[] { false, true })
        {
            var side = window.Where(m => m.Inbound == inbound).ToList();
            var who = inbound ? "LDK" : "we";
            var batches = 0;
            for (var i = 0; i < side.Count; i++)
            {
                if (side[i].Type == (ushort)MessageTypes.CommitmentSigned)
                    Assert.Fail($"{who} sent a commitment_signed outside a batch while the splice was pending "
                              + $"(#{side[i].Sequence})");
                if (side[i].Type != (ushort)MessageTypes.StartBatch)
                    continue;

                var size = fundings.Count;
                Assert.Equal(size, side[i].StartBatchSize);
                Assert.True(i + size < side.Count,
                            $"{who}: start_batch #{side[i].Sequence} is not followed by {size} messages");
                var batch = side.Skip(i + 1).Take(size).ToList();
                Assert.True(batch.All(m => m.Type == (ushort)MessageTypes.CommitmentSigned),
                            $"{who}: start_batch #{side[i].Sequence} is not followed by {size} commitment_signed");
                Assert.Equal(fundings.ToHashSet(), batch.Select(m => m.CommitmentFundingTxId!).ToHashSet());
                batches++;
                i += size;
            }

            var revokes = window.Count(m => m.Inbound != inbound && m.Type == (ushort)MessageTypes.RevokeAndAck);
            Assert.True(revokes == batches, $"{batches} batches from {who} answered by {revokes} revoke_and_ack");
            counts[inbound ? 1 : 0] = batches;
        }

        Console.WriteLine($"[proof] while pending: {counts[0]} batches of ours, {counts[1]} of LDK's");
        Assert.True(counts[0] > 0 && counts[1] > 0, "no batch from one side while the splice was pending");
    }

    private static async Task<ChannelSnapshot> SnapshotAsync(LdkChannelSession session, CancellationToken ct)
    {
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ours = await session.GetOurChannelAsync(ct);
        var ldk = await session.GetLdkChannelAsync(ct);
        Assert.NotNull(ours.FundingTxId);
        Assert.NotNull(ours.FundingOutputIndex);
        var funding = new uint256((byte[])ours.FundingTxId.Value);
        Assert.Equal(funding.ToString(), LdkFundingTxId(ldk));
        return new ChannelSnapshot(funding, ours.FundingOutputIndex.Value, (ulong)ours.Capacity.Satoshi,
                                   (long)ours.LocalBalance.MilliSatoshi, ReadUInt64(ldk["short_channel_id"]));
    }

    /// <summary>
    /// No warning or error either way, except LDK's connection-level peer-storage size warning: since NL-433 our
    /// backup goes to every peer that offers <c>option_provide_storage</c>, and LDK answers the first full-size blob
    /// of each connection with it (NL-559: we then send one that fits), so a long test sees it.
    /// </summary>
    private static void AssertNoWarningOrError(SpliceWireRecorder wire)
    {
        var sent = wire.Snapshot().Where(m => m.Type is SpliceWireRecorder.WarningType
                                                     or SpliceWireRecorder.ErrorType
                                           && !IsPeerStorageSizeWarning(m.Type, m.Hex)).ToList();
        Assert.True(sent.Count == 0,
                    "warnings/errors on the wire: "
                  + string.Join("; ", sent.Select(m => $"{(m.Inbound ? "received" : "sent")} {m.Type} {m.Hex}")));
    }

    /// <summary>
    /// LDK's <c>warning</c> with an all-zero channel id and "Supports only data up to 1024 bytes in peer storage."
    /// </summary>
    private static bool IsPeerStorageSizeWarning(int type, string hex)
    {
        if (type != SpliceWireRecorder.WarningType || hex.Length < 2 * (2 + 32 + 2))
            return false;

        var bytes = Convert.FromHexString(hex);
        if (bytes.AsSpan(2, 32).ContainsAnyExcept((byte)0))
            return false;

        var text = System.Text.Encoding.ASCII.GetString(bytes, 2 + 32 + 2, bytes.Length - (2 + 32 + 2));
        return text.Contains("peer storage", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The channel before the splice.</summary>
    private sealed record ChannelSnapshot(uint256 FundingTxId, uint FundingOutputIndex, ulong CapacitySat,
                                          long LocalBalanceMsat, ulong ShortChannelId);

    /// <summary>The channel once both ends locked the splice.</summary>
    private sealed record LockedChannel(long LocalBalanceMsat, long LockedAtSequence);
}