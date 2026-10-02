using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
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
using SpliceWireMessage = Cln.ClnSpliceTests.SpliceWireMessage;
using SpliceWireRecorder = Cln.ClnSpliceTests.SpliceWireRecorder;

/// <summary>
/// Splicing and quiescence against Eclair 0.14.3 (NL-554), with our default features (<c>option_splice</c>,
/// <c>option_quiesce</c> and <c>option_dual_fund</c> Optional since D13) on the channels of the mainnet day-0 goal: a
/// dual-funded channel (a plain <c>openchannel</c> to Eclair is v2, NL-551, or Eclair's own <c>open_channel2</c>) that
/// is then spliced. (a) we splice in, (b) we splice out to an address, (c) Eclair splices in, (d) Eclair splices out to
/// an address, (e) we RBF our pending splice (<c>bumpsplice</c>), (f) Eclair RBFs its pending splice
/// (<c>rbfsplice</c>), (g) Eclair restarts while our splice is pending. Every splice is checked on the wire (the
/// quiescence initiator's <c>stfu</c> first, <c>splice_init</c>/<c>splice_ack</c>, a <c>commitment_signed</c> each way
/// for the new funding before any <c>tx_signatures</c>, both <c>tx_signatures</c> with the
/// <c>shared_input_signature</c>), on chain (the splice transaction spends the old funding output and creates the new
/// one of the expected capacity) and once locked (both <c>splice_locked</c>, Eclair and we list the new funding
/// outpoint, capacity and short channel id), with payments both ways while it is pending (every
/// <c>commitment_signed</c> in a <c>start_batch</c>) and after the lock.
/// </summary>
/// <remarks>
/// <para><b>What Eclair 0.14.3 offers (its API handlers at the tag):</b> <c>splicein --channelId --amountIn</c> (from
/// its bitcoind wallet), <c>spliceout --channelId --amountOut --address</c> and <c>rbfsplice --channelId
/// --targetFeerateSatByte --fundingFeeBudgetSatoshis</c>. It accepts a peer's splice RBF only
/// <c>eclair.channel.funding.remote-rbf-limits.attempt-delta-blocks</c> (3) blocks after the previous attempt, so the
/// RBF proofs mine empty blocks (<c>generateblock</c> without transactions) before the bump; our own rule (one new block
/// since the latest attempt, NL-520) is met by them too. Eclair takes a peer's splice feerate down to half of its own
/// funding estimate (<c>feerate-tolerance.ratio-low</c>; 5 sat/vB on the idle regtest), so our splices pay at least
/// 2,000 sat/kw.</para>
/// <para>Run with <c>scripts/run-interop.sh eclair Release -class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairSpliceTests</c>.</para>
/// </remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairSpliceTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 12 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time until both ends locked the splice.</summary>
    private const int MaxLockBlocks = 15;

    /// <summary>The feerate of our splices: the test node's 10 sat/vB estimate.</summary>
    private const uint OurFeeRatePerKw = 2_500;

    /// <summary>The first attempt's feerate in our RBF proof (e): above half of Eclair's 5 sat/vB funding estimate.</summary>
    private const uint FirstAttemptFeeRatePerKw = 2_000;

    /// <summary>Eclair's <c>remote-rbf-limits.attempt-delta-blocks</c>.</summary>
    private const int EclairRbfDeltaBlocks = 3;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;
    private SpliceWireRecorder? _wire;

    public EclairSpliceTests(EclairFixture fixture, ITestOutputHelper output)
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
                Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
                await DockerDiagnostics.DumpContainerLogsAsync([EclairFixture.EclairContainerName], 400);
            }

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// (a) We splice in 100,000 sat (<c>splicein</c>, 2,500 sat/kw) on the dual-funded channel we opened: we are the
    /// quiescence initiator, Eclair acks with 0, the transaction pays our feerate; a payment each way while pending; both
    /// lock it, Eclair lists the new funding, capacity and short channel id; payments both ways after, our balance grew
    /// by 100,000.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeSpliceIn_When_TheSpliceLocks_Then_EclairAgreesAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-eclair-splice-a", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        var response = await SpliceInAsync(session, 100_000, ct);

        // Assert: we started it and Eclair acked with 0
        Assert.True(response.State == SpliceNegotiationState.Signed, $"{response.State}: {response.FailureReason}");
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
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, spliceTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (b) We splice 50,000 sat out to an address of bitcoind's wallet (<c>spliceout --address</c>): the transaction
    /// pays exactly 50,000 sat there and funds the channel with the rest; our <c>splice_init</c> carries
    /// -(50,000 + fee), Eclair acks with 0; payments both ways while pending; after the lock the capacity and our balance
    /// dropped by amount + fee on both ends, and payments flow both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_WeSpliceOutToAnAddress_When_TheSpliceLocks_Then_TheAddressIsPaidAndCapacityDrops()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-eclair-splice-b", ct);
        var before = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        var from = wire.CurrentSequence;

        // Act
        var response = await SpliceOutAsync(session, 50_000, address.ToString(), ct);

        // Assert
        Assert.True(response.State == SpliceNegotiationState.Signed, $"{response.State}: {response.FailureReason}");
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
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - (long)(50_000 + fee) * 1_000 - 20_000_000 + 10_000_000,
                     after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, spliceTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (c) Eclair splices 100,000 sat in (<c>splicein</c>) on the dual-funded channel it opened to us: Eclair is the
    /// quiescence initiator and sends <c>splice_init</c> with +100,000, we accept with 0; the capacity grows by Eclair's
    /// contribution and our balance moves only by the payments (both ways while pending and after the lock).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairSplicesIn_When_TheSpliceLocks_Then_WeAcceptAndPaymentsFlow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildEclairFundedAsync("nltg-eclair-splice-c", ct);
        await _fixture.FundEclairWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act
        var answer = await session.Eclair.SpliceInAsync(session.ChannelIdHex, 100_000, ct);
        Console.WriteLine($"[eclair] splicein: {answer?.ToJsonString()}");

        // Assert: Eclair started it, we acked with 0
        var (init, spliceTxId, signaturesAt) = await AssertPeerSpliceAsync(session, wire, from, ct);
        Assert.Equal(100_000, init.SpliceContributionSatoshis);
        var expectedCapacity = (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis);
        await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, null, ct);

        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, spliceTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (d) Eclair splices 50,000 sat out to an address of bitcoind's wallet (<c>spliceout --address</c>): Eclair's
    /// <c>splice_init</c> carries a negative contribution of at least the amount, we accept with 0; the transaction pays
    /// the address, the capacity drops by Eclair's contribution and our balance moves only by the payments.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairSplicesOutToAnAddress_When_TheSpliceLocks_Then_OurBalanceStaysAndTheAddressIsPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildEclairFundedAsync("nltg-eclair-splice-d", ct);
        var before = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        var from = wire.CurrentSequence;

        // Act
        var answer = await session.Eclair.SpliceOutAsync(session.ChannelIdHex, 50_000, address.ToString(), ct);
        Console.WriteLine($"[eclair] spliceout: {answer?.ToJsonString()}");

        // Assert
        var (init, spliceTxId, signaturesAt) = await AssertPeerSpliceAsync(session, wire, from, ct);
        Assert.True(init.SpliceContributionSatoshis <= -50_000,
                    $"Eclair's contribution {init.SpliceContributionSatoshis} is above -50,000");
        var expectedCapacity = (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis);
        var tx = await AssertSpliceTransactionAsync(before, spliceTxId, expectedCapacity, null, ct);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey && o.Value == Money.Satoshis(50_000));
        Console.WriteLine($"[proof] Eclair's contribution {init.SpliceContributionSatoshis}, fee "
                        + $"{await GetFeeAsync(spliceTxId, ct)} sat");

        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, spliceTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (e) Splice RBF, ours: we splice in 100,000 sat at 2,000 sat/kw, three empty blocks pass (Eclair's RBF delta),
    /// then <c>bumpsplice</c> at 2,500 sat/kw: we are the quiescence initiator of the RBF and send <c>tx_init_rbf</c> at
    /// 2,500; Eclair answers <c>tx_ack_rbf</c>; the new attempt is signed both ways and replaces the first in bitcoind's
    /// mempool; payments while both attempts are pending go in batches of three; the bumped attempt locks on both ends.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingSplice_When_WeBumpIt_Then_EclairFollowsTheRbfAndTheBumpLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-eclair-splice-e", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var first = await SpliceInAsync(session, 100_000, ct, FirstAttemptFeeRatePerKw);
        Assert.True(first.State == SpliceNegotiationState.Signed, $"{first.State}: {first.FailureReason}");
        Assert.NotNull(first.SpliceTxId);
        var firstTxId = new uint256((byte[])first.SpliceTxId.Value);
        await AssertSignedSpliceAsync(session, wire, from, firstTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await MineEmptyBlocksAsync(session, EclairRbfDeltaBlocks, ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        var bumped = await BumpAsync(session, OurFeeRatePerKw, ct);

        // Assert: our tx_init_rbf at the new feerate, Eclair acked, the bump replaced the first attempt
        Assert.True(bumped.State == SpliceNegotiationState.Signed, $"the bump is {bumped.State}: {bumped.FailureReason}");
        Assert.NotNull(bumped.SpliceTxId);
        var bumpTxId = new uint256((byte[])bumped.SpliceTxId.Value);
        Assert.NotEqual(firstTxId, bumpTxId);
        var initRbf = wire.First(inbound: false, MessageTypes.TxInitRbf, rbfFrom);
        var ackRbf = wire.First(inbound: true, MessageTypes.TxAckRbf, rbfFrom);
        Assert.Equal(OurFeeRatePerKw, RbfFeerate(initRbf));
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        AssertQuiescence(wire, rbfFrom, weInitiated: true, initRbf.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, rbfFrom, bumpTxId, ct);
        await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, OurFeeRatePerKw, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        Assert.True(await GetFeeAsync(bumpTxId, ct) > firstFee, "the bump pays no more than the first attempt");

        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (f) Splice RBF, Eclair's: Eclair splices in 100,000 sat, one empty block passes (our one-block rule, NL-520), then
    /// <c>rbfsplice</c> at twice its first feerate. We get Eclair's <c>stfu</c> and <c>tx_init_rbf</c>, answer
    /// <c>tx_ack_rbf</c>; the new attempt is signed both ways and replaces the first; the bump locks on both ends with our
    /// balance moved only by the payments.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairsPendingSplice_When_EclairBumpsIt_Then_WeFollowTheRbfAndTheBumpLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildEclairFundedAsync("nltg-eclair-splice-f", ct);
        await _fixture.FundEclairWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        Console.WriteLine($"[eclair] splicein: {(await session.Eclair.SpliceInAsync(session.ChannelIdHex, 100_000, ct))
           ?.ToJsonString()}");
        var (init, firstTxId, _) = await AssertPeerSpliceAsync(session, wire, from, ct);
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await MineEmptyBlocksAsync(session, 1, ct);
        var rbfFrom = wire.CurrentSequence;
        var targetSatByte = Math.Max(2 * (long)init.SpliceFeeratePerKw / 250, (long)init.SpliceFeeratePerKw / 250 + 2);

        // Act
        var answer = await session.Eclair.RbfSpliceAsync(session.ChannelIdHex, targetSatByte, 20_000, ct);
        Console.WriteLine($"[eclair] rbfsplice at {targetSatByte} sat/vB: {answer?.ToJsonString()}");

        // Assert
        var initRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxInitRbf, rbfFrom),
                                          s_stepTimeout, "Eclair's tx_init_rbf", ct);
        var ackRbf = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxAckRbf, rbfFrom),
                                         s_stepTimeout, "our tx_ack_rbf", ct);
        Console.WriteLine($"[proof] Eclair's tx_init_rbf: feerate {RbfFeerate(initRbf)} sat/kw (first attempt "
                        + $"{init.SpliceFeeratePerKw})");
        Assert.True(RbfFeerate(initRbf) > init.SpliceFeeratePerKw);
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        AssertQuiescence(wire, rbfFrom, weInitiated: false, initRbf.Sequence);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures,
                                                                      rbfFrom),
                                            s_stepTimeout, "Eclair's tx_signatures for the bump", ct);
        var bumpTxId = theirSigs.TxSignaturesTxId;
        Assert.NotEqual(firstTxId, bumpTxId);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, rbfFrom, bumpTxId, ct);
        var bumpTx = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(bumpTxId, true, ct);
        var bumpFunding = Assert.Single(bumpTx.Outputs, o => o.ScriptPubKey.IsScriptType(ScriptType.P2WSH));
        var expectedCapacity = (ulong)bumpFunding.Value.Satoshi;
        Assert.Equal(before.CapacitySat + 100_000, expectedCapacity);
        await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, null, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        Assert.True(await GetFeeAsync(bumpTxId, ct) > firstFee, "Eclair's bump pays no more than its first attempt");

        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (g) Our splice-in is signed and pending when Eclair restarts: we reconnect, both send
    /// <c>channel_reestablish</c>, the channel is usable on the old and pending fundings (a payment each way), and the
    /// splice then locks on both ends with payments after.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingSplice_When_EclairRestarts_Then_ReestablishedAndTheSpliceLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-eclair-splice-g", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var response = await SpliceInAsync(session, 100_000, ct);
        Assert.True(response.State == SpliceNegotiationState.Signed, $"{response.State}: {response.FailureReason}");
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        var restartedAt = wire.CurrentSequence;

        // Act
        await _fixture.RestartEclairAsync(ct);

        // Assert: reestablished both ways, payments on the pending splice, then the lock
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish, restartedAt),
                            EclairChannelSession.UsableTimeout, "Eclair's channel_reestablish", ct);
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish, restartedAt),
                            EclairChannelSession.UsableTimeout, "our channel_reestablish", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        Assert.False((await session.GetOurChannelAsync(ct)).DataLossDetected);
        Assert.Contains(spliceTxId, await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct));
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// The dual-funded channel we open to Eclair (a plain <c>openchannel</c>, 1M sat, NL-551) with our traffic recorded,
    /// and 200,000 sat paid to Eclair so it can pay back and keep its reserve.
    /// </summary>
    private async Task<(EclairChannelSession Session, SpliceWireRecorder Wire)> BuildOurFundedAsync(
        string nodeName, CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        _session = await EclairChannelSession.BuildOurFundedAsync(_fixture, nodeName, s_capacity, null, ct,
                                                                  configureNode: node => node.ConfigureServices = wire.Install);
        AssertSpliceNegotiated(_session);
        await _session.AssertWePayEclairAsync(LightningMoney.Satoshis(200_000), ct);
        return (_session, wire);
    }

    /// <summary>
    /// The dual-funded channel Eclair opens to us (1M sat, <c>open_channel2</c>) with our traffic recorded, and
    /// 200,000 sat paid to us so we can pay Eclair.
    /// </summary>
    private async Task<(EclairChannelSession Session, SpliceWireRecorder Wire)> BuildEclairFundedAsync(
        string nodeName, CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        _session = await EclairChannelSession.BuildEclairFundedAsync(_fixture, nodeName, s_capacity, ct,
                                                                     configureNode: node => node.ConfigureServices = wire.Install);
        AssertSpliceNegotiated(_session);
        await _session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(200_000), ct);
        return (_session, wire);
    }

    /// <summary>Eclair advertises 35 (<c>option_quiesce</c>) and 63 (<c>option_splice</c>) and so do we.</summary>
    private static void AssertSpliceNegotiated(EclairChannelSession session)
    {
        var peer = session.Node.PeerManager.GetPeer(session.EclairPubKey);
        Assert.NotNull(peer);
        foreach (var feature in new[] { Feature.OptionQuiesce, Feature.OptionSplice })
            Assert.True(peer.Features.IsFeatureSet(feature), $"Eclair does not advertise {feature}");
    }

    private static async Task<SpliceClientResponse> SpliceInAsync(EclairChannelSession session, ulong amountSat,
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

    private static async Task<SpliceClientResponse> SpliceOutAsync(EclairChannelSession session, ulong amountSat,
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
    private static async Task<SpliceClientResponse> BumpAsync(EclairChannelSession session, uint feeRatePerKw,
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

    /// <summary>
    /// <paramref name="count"/> empty blocks (<c>generateblock</c> without transactions): the pending splice stays
    /// unconfirmed while the RBF rules' block counts move.
    /// </summary>
    private async Task MineEmptyBlocksAsync(EclairChannelSession session, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
            await _fixture.Bitcoin.Rpc.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        }

        await _fixture.WaitAllAtTipAsync([session.Node], ct);
    }

    /// <summary><c>tx_init_rbf</c>: <c>feerate</c> after <c>channel_id</c> and <c>locktime</c>.</summary>
    private static uint RbfFeerate(SpliceWireMessage message) =>
        BinaryPrimitives.ReadUInt32BigEndian(message.Wire.AsSpan(2 + 32 + 4, 4));

    private async Task AssertReplacedAsync(uint256 replacedTxId, uint256 bumpTxId, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Contains(bumpTxId) && !mempool.Contains(replacedTxId);
        }, s_stepTimeout, $"{bumpTxId} replaced {replacedTxId} in bitcoind's mempool", ct);

    /// <summary>
    /// The quiescence of a splice from <paramref name="from"/>: the initiator's <c>stfu</c> (initiator = 1) first, the
    /// other side's (initiator = 0) after it, both before <c>splice_init</c> (or <c>tx_init_rbf</c>) at
    /// <paramref name="initSequence"/>.
    /// </summary>
    private static void AssertQuiescence(SpliceWireRecorder wire, long from, bool weInitiated, long initSequence)
    {
        var ours = wire.First(inbound: false, MessageTypes.Stfu, from);
        var theirs = wire.First(inbound: true, MessageTypes.Stfu, from);
        var (first, second) = weInitiated ? (ours, theirs) : (theirs, ours);
        Assert.True(first.StfuInitiator && !second.StfuInitiator,
                    $"{(weInitiated ? "we were" : "Eclair was")} not the quiescence initiator");
        Assert.True(first.Sequence < second.Sequence && second.Sequence < initSequence,
                    "the stfu exchange did not come before splice_init");
    }

    /// <summary>
    /// A splice Eclair started: Eclair's <c>stfu</c> and <c>splice_init</c>, our <c>splice_ack</c> with 0, then the
    /// signed splice. Returns Eclair's <c>splice_init</c>, the splice txid and the sequence the splice is pending from.
    /// </summary>
    private async Task<(SpliceWireMessage Init, uint256 SpliceTxId, long SignaturesAt)> AssertPeerSpliceAsync(
        EclairChannelSession session, SpliceWireRecorder wire, long from, CancellationToken ct)
    {
        var init = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceInit, from),
                                       s_stepTimeout, "Eclair's splice_init", ct);
        var ack = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceAck, from),
                                      s_stepTimeout, "our splice_ack", ct);
        Console.WriteLine($"[proof] Eclair's splice_init: contribution {init.SpliceContributionSatoshis} sat, feerate "
                        + $"{init.SpliceFeeratePerKw} sat/kw");
        Assert.Equal(0, ack.SpliceContributionSatoshis);
        Assert.True(init.Sequence < ack.Sequence);
        AssertQuiescence(wire, from, weInitiated: false, init.Sequence);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from),
                                            s_stepTimeout, "Eclair's tx_signatures", ct);
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
    private async Task<long> AssertSignedSpliceAsync(EclairChannelSession session, SpliceWireRecorder wire, long from,
                                                     uint256 spliceTxId, CancellationToken ct)
    {
        var ourSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.TxSignatures, from),
                                          s_stepTimeout, "our tx_signatures", ct);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures, from),
                                            s_stepTimeout, "Eclair's tx_signatures", ct);
        foreach (var sigs in new[] { ourSigs, theirSigs })
        {
            Assert.Equal(spliceTxId, sigs.TxSignaturesTxId);
            Assert.True(sigs.HasSharedInputSignature,
                        $"{(sigs.Inbound ? "Eclair's" : "our")} tx_signatures has no shared_input_signature");
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
    /// Mines one block at a time until both ends moved to the splice's funding: Eclair's only active commitment and ours
    /// are the splice, both sent <c>splice_locked</c> for it, both list the new capacity and the same new short channel
    /// id, and the channel is usable again.
    /// </summary>
    private async Task<LockedChannel> MineUntilLockedAsync(EclairChannelSession session, SpliceWireRecorder wire,
                                                           ChannelSnapshot before, uint256 spliceTxId,
                                                           ulong expectedCapacitySat, CancellationToken ct)
    {
        for (var block = 1; ; block++)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var eclair = await session.GetEclairChannelAsync(ct);
            var ours = await session.GetOurChannelAsync(ct);
            Console.WriteLine($"[proof] +{block} block(s): ours {ours.Describe()} funding "
                            + $"{(ours.FundingTxId is { } f ? new uint256((byte[])f) : null)}; eclair "
                            + $"{EclairChannelSession.DescribeEclair(eclair)} active {ActiveFundings(eclair)}");
            if (ActiveCount(eclair) == 1 && EclairFundingTxId(eclair) == spliceTxId.ToString()
             && ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId
             && wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked) is not null)
                break;

            Assert.True(block < MaxLockBlocks, $"the splice was not locked by both ends in {MaxLockBlocks} blocks");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var ourLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceLocked),
                                            s_stepTimeout, "our splice_locked", ct);
        var theirLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked),
                                              s_stepTimeout, "Eclair's splice_locked", ct);
        Assert.Equal(spliceTxId, ourLocked.SpliceLockedTxId);
        Assert.Equal(spliceTxId, theirLocked.SpliceLockedTxId);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);

        var channel = await session.GetOurChannelAsync(ct);
        var eclairChannel = await session.GetEclairChannelAsync(ct);
        Console.WriteLine($"[proof] locked: ours {channel.Describe()}; eclair "
                        + $"{EclairChannelSession.DescribeEclair(eclairChannel)} active {ActiveFundings(eclairChannel)}");
        Assert.Equal((long)expectedCapacitySat, channel.Capacity.Satoshi);
        Assert.Equal((long)expectedCapacitySat, Active(eclairChannel)!["fundingAmount"]!.GetValue<long>());
        Assert.NotNull(channel.ShortChannelId);
        var eclairScid = EclairShortChannelId(eclairChannel);
        Assert.NotNull(eclairScid);
        Assert.NotEqual(before.ShortChannelId, eclairScid);
        Assert.Equal(eclairScid, channel.ShortChannelId.Value.ToUInt64());
        Assert.Equal((long)expectedCapacitySat * 1_000,
                     (long)(channel.LocalBalance.MilliSatoshi + channel.RemoteBalance.MilliSatoshi));
        return new LockedChannel((long)channel.LocalBalance.MilliSatoshi,
                                 Math.Max(ourLocked.Sequence, theirLocked.Sequence));
    }

    private static JsonNode? Active(JsonNode eclair) => EclairJson.Active(eclair);

    private static int ActiveCount(JsonNode eclair) => EclairJson.ActiveCount(eclair);

    private static string ActiveFundings(JsonNode eclair) => EclairJson.DescribeActive(eclair);

    private static string? EclairFundingTxId(JsonNode eclair) => EclairJson.FundingTxId(eclair);

    private static ulong? EclairShortChannelId(JsonNode eclair) => EclairJson.ShortChannelId(eclair);

    /// <summary>
    /// While the splice was pending (between <paramref name="from"/> and <paramref name="to"/>) every
    /// <c>commitment_signed</c> of each side came in a <c>start_batch</c> of one per active funding in
    /// <paramref name="fundings"/> (the current one and each pending attempt), answered by one <c>revoke_and_ack</c>.
    /// </summary>
    private static void AssertBatchesWhilePending(SpliceWireRecorder wire, long from, long to,
                                                  IReadOnlyList<uint256> fundings)
    {
        var window = wire.Snapshot().Where(m => m.Sequence > from && m.Sequence < to && !m.IsPingOrPong).ToList();
        var counts = new int[2];
        foreach (var inbound in new[] { false, true })
        {
            var side = window.Where(m => m.Inbound == inbound).ToList();
            var who = inbound ? "Eclair" : "we";
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

        Console.WriteLine($"[proof] while pending: {counts[0]} batches of ours, {counts[1]} of Eclair's");
        Assert.True(counts[0] > 0 && counts[1] > 0, "no batch from one side while the splice was pending");
    }

    private async Task<ChannelSnapshot> SnapshotAsync(EclairChannelSession session, CancellationToken ct)
    {
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ours = await session.GetOurChannelAsync(ct);
        // Eclair, as the funder of a dual-funded channel, is NORMAL before its funding status shows the confirmation
        // (and the short channel id): that comes with a later block
        JsonNode? eclair = null;
        for (var block = 0; block < MaxLockBlocks; block++)
        {
            eclair = await session.GetEclairChannelAsync(ct);
            if (EclairShortChannelId(eclair) is not null)
                break;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            eclair = await session.GetEclairChannelAsync(ct);
            if (EclairShortChannelId(eclair) is not null)
                break;

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
        }

        Assert.NotNull(eclair);
        Assert.True(EclairShortChannelId(eclair) is not null,
                    $"Eclair lists no short channel id: {EclairJson.DescribeActive(eclair)}");
        Assert.NotNull(ours.FundingTxId);
        Assert.NotNull(ours.FundingOutputIndex);
        var funding = new uint256((byte[])ours.FundingTxId.Value);
        Assert.Equal(funding.ToString(), EclairFundingTxId(eclair));
        Assert.Equal(1, ActiveCount(eclair));
        return new ChannelSnapshot(funding, ours.FundingOutputIndex.Value, (ulong)ours.Capacity.Satoshi,
                                   (long)ours.LocalBalance.MilliSatoshi,
                                   EclairShortChannelId(eclair)!.Value);
    }

    private static void AssertNoWarningOrError(SpliceWireRecorder wire)
    {
        var sent = wire.Snapshot().Where(m => m.Type is SpliceWireRecorder.WarningType
                                                     or SpliceWireRecorder.ErrorType).ToList();
        Assert.True(sent.Count == 0,
                    "warnings/errors on the wire: "
                  + string.Join("; ", sent.Select(m => $"{(m.Inbound ? "received" : "sent")} {m.Type} {m.Hex}")));
    }

    /// <summary>The channel before the splice.</summary>
    private sealed record ChannelSnapshot(uint256 FundingTxId, uint FundingOutputIndex, ulong CapacitySat,
                                          long LocalBalanceMsat, ulong ShortChannelId);

    /// <summary>The channel once both ends locked the splice.</summary>
    private sealed record LockedChannel(long LocalBalanceMsat, long LockedAtSequence);
}