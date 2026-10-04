using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Abcd;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Protocol.Constants;
using Fixtures;
using Utils;
using SpliceWireMessage = Cln.ClnSpliceTests.SpliceWireMessage;
using SpliceWireRecorder = Cln.ClnSpliceTests.SpliceWireRecorder;

/// <summary>
/// Splicing simple taproot channels against Eclair 0.14.3 (taproot wave t03, lane ECL2; NL-877, NL-965): Eclair
/// advertises <c>option_simple_taproot</c> and splices taproot channels, our node runs on the experimental gate
/// (<see cref="EclairTaprootTests.EnableTaproot"/>). Every splice is checked on the wire (quiescence first,
/// <c>splice_init</c>/<c>splice_ack</c>, a <c>commitment_signed</c> each way for the new funding before any
/// <c>tx_signatures</c>, both <c>tx_signatures</c> with the MuSig2 <c>shared_input_partial_signature</c> (TLV 2, no
/// ECDSA TLV 0)), on chain (the splice transaction spends the previous P2TR funding by key path, one 64-byte witness
/// element, and creates the new P2TR funding of the expected capacity) and once locked (both <c>splice_locked</c>, the
/// new funding, capacity and short channel id on both ends), with payments both ways while pending (every
/// <c>commitment_signed</c> in a <c>start_batch</c>) and after the lock. (a) We splice in and then out of the taproot
/// channel Eclair opened to us, and close it; (b) Eclair splices in and then out of the taproot channel we opened to
/// it, and closes it; (c) our <c>bumpsplice</c>; (d) Eclair's <c>rbfsplice</c>; (e) our restart and Eclair's restart
/// while our splice is pending (the type-22 nonces name both fundings).
/// </summary>
/// <remarks>
/// Eclair accepts a peer's splice RBF only three blocks after the previous attempt and takes a peer's splice feerate down
/// to half of its own estimate, as <see cref="EclairSpliceTests"/> notes. Run with <c>scripts/run-cluster.sh -n 1 --suite
/// eclair2 --class NLightning.Integration.Tests.Docker.Interop.Eclair.EclairTaprootSpliceTests</c>.
/// </remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairTaprootSpliceTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 15 * 60 * 1_000;

    /// <summary>The most blocks mined one at a time until both ends locked the splice.</summary>
    private const int MaxLockBlocks = 15;

    /// <summary>The feerate of our splices: the test node's 10 sat/vB estimate.</summary>
    private const uint OurFeeRatePerKw = 2_500;

    /// <summary>The first attempt's feerate in our RBF proof (c): above half of Eclair's 5 sat/vB funding estimate.</summary>
    private const uint FirstAttemptFeeRatePerKw = 2_000;

    /// <summary>Eclair's <c>remote-rbf-limits.attempt-delta-blocks</c>.</summary>
    private const int EclairRbfDeltaBlocks = 3;

    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(120);

    private readonly EclairFixture _fixture;
    private EclairChannelSession? _session;
    private SpliceWireRecorder? _wire;

    public EclairTaprootSpliceTests(EclairFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_wire is not null)
            Console.WriteLine($"[wire] {_wire.Describe()}");

        if (_session is null)
            return;

        if (TestDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            foreach (var line in _session.Node.NodeLog.TakeLast(400))
                Console.WriteLine(line);
            await _fixture.DumpEclairLogAsync(400);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) On the taproot channel Eclair opened to us (200,000 sat paid to us first): we splice 100,000 sat in from our
    /// wallet, then 50,000 sat out to an address of bitcoind's wallet; each splice is signed with the shared input's
    /// MuSig2 partial signatures, locks on both ends and carries payments both ways while pending and after; then our
    /// cooperative close of the spliced channel confirms by key path.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairsTaprootChannel_When_WeSpliceInThenOutAndClose_Then_EachSpliceLocksAndTheCloseConfirms()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildEclairFundedAsync("nltg-tap-splice-a", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act 1: splice in
        var spliceIn = await SpliceInAsync(session, 100_000, ct);

        // Assert 1
        Assert.True(spliceIn.State == SpliceNegotiationState.Signed, $"{spliceIn.State}: {spliceIn.FailureReason}");
        Assert.NotNull(spliceIn.SpliceTxId);
        var spliceInTxId = new uint256((byte[])spliceIn.SpliceTxId.Value);
        var capacityIn = before.CapacitySat + 100_000;
        Assert.Equal(capacityIn, spliceIn.NewCapacitySat);
        var init = wire.First(inbound: false, MessageTypes.SpliceInit, from);
        Assert.Equal(100_000, init.SpliceContributionSatoshis);
        Assert.Equal(0, wire.First(inbound: true, MessageTypes.SpliceAck, from).SpliceContributionSatoshis);
        AssertQuiescence(wire, from, weInitiated: true, init.Sequence);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceInTxId, ct);
        await AssertSpliceTransactionAsync(before, spliceInTxId, capacityIn, OurFeeRatePerKw, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var locked = await MineUntilLockedAsync(session, wire, before, spliceInTxId, capacityIn, from, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, locked.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, locked.LockedAtSequence, [before.FundingTxId, spliceInTxId]);

        // Act 2: splice out
        var middle = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        from = wire.CurrentSequence;
        var spliceOut = await SpliceOutAsync(session, 50_000, address.ToString(), ct);

        // Assert 2
        Assert.True(spliceOut.State == SpliceNegotiationState.Signed, $"{spliceOut.State}: {spliceOut.FailureReason}");
        Assert.NotNull(spliceOut.SpliceTxId);
        var spliceOutTxId = new uint256((byte[])spliceOut.SpliceTxId.Value);
        init = wire.First(inbound: false, MessageTypes.SpliceInit, from);
        AssertQuiescence(wire, from, weInitiated: true, init.Sequence);
        signaturesAt = await AssertSignedSpliceAsync(session, wire, from, spliceOutTxId, ct);
        var fee = await GetFeeAsync(spliceOutTxId, ct);
        Assert.Equal(-(long)(50_000 + fee), init.SpliceContributionSatoshis);
        var capacityOut = middle.CapacitySat - 50_000 - fee;
        Assert.Equal(capacityOut, spliceOut.NewCapacitySat);
        var tx = await AssertSpliceTransactionAsync(middle, spliceOutTxId, capacityOut, OurFeeRatePerKw, ct);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey && o.Value == Money.Satoshis(50_000));
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        locked = await MineUntilLockedAsync(session, wire, middle, spliceOutTxId, capacityOut, from, ct);
        Assert.Equal(middle.LocalBalanceMsat - (long)(50_000 + fee) * 1_000 - 20_000_000 + 10_000_000,
                     locked.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, locked.LockedAtSequence, [middle.FundingTxId, spliceOutTxId]);
        AssertNoWarningOrError(wire);

        // Act 3 and Assert 3: our cooperative close of the spliced channel
        await EclairTaprootTests.WeCloseAsync(_fixture, session, ct);
    }

    /// <summary>
    /// (b) On the taproot channel we opened to Eclair (300,000 sat paid to Eclair first): Eclair splices 100,000 sat in
    /// from its wallet, then 50,000 sat out to an address of bitcoind's wallet; we accept each with 0, both lock and our
    /// balance moves only by the payments; then Eclair's cooperative close of the spliced channel confirms by key path.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurTaprootChannel_When_EclairSplicesInThenOutAndCloses_Then_EachSpliceLocksAndTheCloseConfirms()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-tap-splice-b", ct);
        await _fixture.FundEclairWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;

        // Act 1: Eclair splices in
        Console.WriteLine($"[eclair] splicein: {(await session.Eclair.SpliceInAsync(session.ChannelIdHex, 100_000, ct))
           ?.ToJsonString()}");

        // Assert 1
        var (init, spliceInTxId, signaturesAt) = await AssertPeerSpliceAsync(session, wire, from, ct);
        Assert.Equal(100_000, init.SpliceContributionSatoshis);
        var capacityIn = (ulong)((long)before.CapacitySat + init.SpliceContributionSatoshis);
        await AssertSpliceTransactionAsync(before, spliceInTxId, capacityIn, null, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var locked = await MineUntilLockedAsync(session, wire, before, spliceInTxId, capacityIn, from, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, locked.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, locked.LockedAtSequence, [before.FundingTxId, spliceInTxId]);

        // Act 2: Eclair splices out
        var middle = await SnapshotAsync(session, ct);
        var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
        from = wire.CurrentSequence;
        Console.WriteLine($"[eclair] spliceout: {(await session.Eclair.SpliceOutAsync(session.ChannelIdHex, 50_000,
                                                                                         address.ToString(), ct))
           ?.ToJsonString()}");

        // Assert 2
        (init, var spliceOutTxId, signaturesAt) = await AssertPeerSpliceAsync(session, wire, from, ct);
        Assert.True(init.SpliceContributionSatoshis <= -50_000,
                    $"Eclair's contribution {init.SpliceContributionSatoshis} is above -50,000");
        var capacityOut = (ulong)((long)middle.CapacitySat + init.SpliceContributionSatoshis);
        var tx = await AssertSpliceTransactionAsync(middle, spliceOutTxId, capacityOut, null, ct);
        Assert.Single(tx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey && o.Value == Money.Satoshis(50_000));
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        locked = await MineUntilLockedAsync(session, wire, middle, spliceOutTxId, capacityOut, from, ct);
        Assert.Equal(middle.LocalBalanceMsat - 20_000_000 + 10_000_000, locked.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, locked.LockedAtSequence, [middle.FundingTxId, spliceOutTxId]);
        AssertNoWarningOrError(wire);

        // Act 3 and Assert 3: Eclair's cooperative close of the spliced channel
        await EclairTaprootTests.EclairClosesAsync(_fixture, session, ct);
    }

    /// <summary>
    /// (c) Splice RBF, ours, on a taproot channel: we splice in 100,000 sat at 2,000 sat/kw, three empty blocks pass, then
    /// <c>bumpsplice</c> at 2,500 sat/kw: our <c>tx_init_rbf</c>, Eclair's <c>tx_ack_rbf</c>, the new attempt signed
    /// with its own nonces replaces the first in bitcoind's mempool; payments go in batches of three while both attempts
    /// are pending; the bump locks on both ends.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingTaprootSplice_When_WeBumpIt_Then_EclairFollowsTheRbfAndTheBumpLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildEclairFundedAsync("nltg-tap-splice-c", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var first = await SpliceInAsync(session, 100_000, ct, FirstAttemptFeeRatePerKw);
        Assert.True(first.State == SpliceNegotiationState.Signed, $"{first.State}: {first.FailureReason}");
        Assert.NotNull(first.SpliceTxId);
        var firstTxId = new uint256((byte[])first.SpliceTxId.Value);
        await AssertSignedSpliceAsync(session, wire, from, firstTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await EclairTaprootTests.MineEmptyBlocksAsync(_fixture, session, EclairRbfDeltaBlocks, ct);
        var rbfFrom = wire.CurrentSequence;

        // Act
        var bumped = await BumpAsync(session, OurFeeRatePerKw, ct);

        // Assert
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
        var after = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, from, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (d) Splice RBF, Eclair's, on a taproot channel: Eclair splices in 100,000 sat, one empty block passes (our
    /// one-block rule, NL-520), then <c>rbfsplice</c> at twice its first feerate; we answer <c>tx_ack_rbf</c>, the new
    /// attempt is signed both ways and replaces the first, and the bump locks on both ends.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairsPendingTaprootSplice_When_EclairBumpsIt_Then_WeFollowTheRbfAndTheBumpLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildOurFundedAsync("nltg-tap-splice-d", ct);
        await _fixture.FundEclairWalletAsync(LightningMoney.Satoshis(300_000), [session.Node], ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        Console.WriteLine($"[eclair] splicein: {(await session.Eclair.SpliceInAsync(session.ChannelIdHex, 100_000, ct))
           ?.ToJsonString()}");
        var (init, firstTxId, _) = await AssertPeerSpliceAsync(session, wire, from, ct);
        var firstFee = await GetFeeAsync(firstTxId, ct);
        await EclairTaprootTests.MineEmptyBlocksAsync(_fixture, session, 1, ct);
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
        Assert.True(RbfFeerate(initRbf) > init.SpliceFeeratePerKw);
        Assert.True(initRbf.Sequence < ackRbf.Sequence);
        AssertQuiescence(wire, rbfFrom, weInitiated: false, initRbf.Sequence);
        var theirSigs = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.TxSignatures,
                                                                      rbfFrom),
                                            s_stepTimeout, "Eclair's tx_signatures for the bump", ct);
        var bumpTxId = theirSigs.TxSignaturesTxId;
        Assert.NotEqual(firstTxId, bumpTxId);
        var signaturesAt = await AssertSignedSpliceAsync(session, wire, rbfFrom, bumpTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;
        await AssertSpliceTransactionAsync(before, bumpTxId, expectedCapacity, null, ct);
        await AssertReplacedAsync(firstTxId, bumpTxId, ct);
        Assert.True(await GetFeeAsync(bumpTxId, ct) > firstFee, "Eclair's bump pays no more than its first attempt");

        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, bumpTxId, expectedCapacity, from, ct);
        Assert.Equal(before.LocalBalanceMsat - 20_000_000 + 10_000_000, after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertBatchesWhilePending(wire, signaturesAt, after.LockedAtSequence, [before.FundingTxId, firstTxId, bumpTxId]);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// (e) Our splice-in of the taproot channel Eclair opened is signed and pending when our node restarts, and again
    /// when Eclair restarts: each time both send <c>channel_reestablish</c> (Eclair fails a taproot channel whose
    /// type-22 nonces miss a funding), the channel is usable on both fundings (a payment each way, in batches of two),
    /// and the splice then locks on both ends with payments after.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurPendingTaprootSplice_When_WeAndEclairRestart_Then_ReestablishedAndTheSpliceLocks()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, wire) = await BuildEclairFundedAsync("nltg-tap-splice-e", ct);
        var before = await SnapshotAsync(session, ct);
        var from = wire.CurrentSequence;
        var response = await SpliceInAsync(session, 100_000, ct);
        Assert.True(response.State == SpliceNegotiationState.Signed, $"{response.State}: {response.FailureReason}");
        Assert.NotNull(response.SpliceTxId);
        var spliceTxId = new uint256((byte[])response.SpliceTxId.Value);
        await AssertSignedSpliceAsync(session, wire, from, spliceTxId, ct);
        var expectedCapacity = before.CapacitySat + 100_000;

        // Act 1: our restart
        var reestablished = EclairTaprootTests.CountReestablished(session);
        var balance = (await session.GetOurChannelAsync(ct)).LocalBalance;
        var restartedAt = wire.CurrentSequence;
        await session.Node.StopAsync();
        await session.StartNodeAsync(ct);

        // Assert 1
        await AssertBothReestablishedAsync(wire, restartedAt, ct);
        await EclairTaprootTests.AssertReestablishedAsync(session, reestablished, balance, ct);
        Assert.Contains(spliceTxId, await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct));
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(10_000), ct);

        // Act 2: Eclair's restart
        reestablished = EclairTaprootTests.CountReestablished(session);
        balance = (await session.GetOurChannelAsync(ct)).LocalBalance;
        restartedAt = wire.CurrentSequence;
        await _fixture.RestartEclairAsync(ct);

        // Assert 2
        await AssertBothReestablishedAsync(wire, restartedAt, ct);
        await EclairTaprootTests.AssertReestablishedAsync(session, reestablished, balance, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(7_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(3_000), ct);
        var after = await MineUntilLockedAsync(session, wire, before, spliceTxId, expectedCapacity, from, ct);
        Assert.Equal(before.LocalBalanceMsat + 100_000_000 - 20_000_000 + 10_000_000 - 7_000_000 + 3_000_000,
                     after.LocalBalanceMsat);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(21_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
        AssertNoWarningOrError(wire);
    }

    /// <summary>
    /// The taproot channel Eclair opens to us (1M sat, <c>open_channel2</c>) with our traffic recorded, and 200,000 sat
    /// paid to us so we can pay Eclair.
    /// </summary>
    private async Task<(EclairChannelSession Session, SpliceWireRecorder Wire)> BuildEclairFundedAsync(
        string nodeName, CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        _session = await EclairChannelSession.BuildEclairFundedAsync(
                       _fixture, nodeName, s_capacity, ct, EclairTaprootTests.EnableTaproot,
                       configureNode: node => node.ConfigureServices = wire.Install,
                       channelType: EclairTaprootTests.EclairTaprootChannelType);
        await EclairTaprootTests.AssertTaprootChannelAsync(_fixture, _session, weAreInitiator: false, s_capacity, ct);
        await _session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(200_000), ct);
        return (_session, wire);
    }

    /// <summary>
    /// The taproot channel we open to Eclair (<c>openchannel --channel-type taproot</c>, 1M sat, v2) with our traffic
    /// recorded, and 300,000 sat paid to Eclair so it can splice out and pay back.
    /// </summary>
    private async Task<(EclairChannelSession Session, SpliceWireRecorder Wire)> BuildOurFundedAsync(
        string nodeName, CancellationToken ct)
    {
        var wire = new SpliceWireRecorder();
        _wire = wire;
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, nodeName, LightningMoney.Satoshis(2_500_000), ct,
                                     EclairTaprootTests.EnableTaproot,
                                     configureNode: node => node.ConfigureServices = wire.Install);
        var opened = await EclairTaprootTests.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(session.EclairAddress, s_capacity)
                         {
                             IsSimpleTaproot = true
                         }, ct);
        session.ChannelId = opened.ChannelId;
        await session.MineUntilUsableAsync(ct);
        await EclairTaprootTests.AssertTaprootChannelAsync(_fixture, session, weAreInitiator: true, s_capacity, ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(300_000), ct);
        return (session, wire);
    }

    private static async Task<SpliceClientResponse> SpliceInAsync(EclairChannelSession session, ulong amountSat,
                                                                  CancellationToken ct,
                                                                  uint feeRatePerKw = OurFeeRatePerKw)
    {
        var response = await EclairTaprootTests.HandleAsync<SpliceInClientRequest, SpliceClientResponse>(
                           session, new SpliceInClientRequest(session.ChannelId, amountSat)
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
        var response = await EclairTaprootTests.HandleAsync<SpliceOutClientRequest, SpliceClientResponse>(
                           session, new SpliceOutClientRequest(session.ChannelId, amountSat)
                           {
                               Address = address,
                               FeeRatePerKw = OurFeeRatePerKw
                           }, ct);
        Console.WriteLine($"[nltg] spliceout: {response.State}, txid {response.SpliceTxId}, capacity "
                        + $"{response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    private static async Task<SpliceClientResponse> BumpAsync(EclairChannelSession session, uint feeRatePerKw,
                                                              CancellationToken ct)
    {
        var response = await EclairTaprootTests.HandleAsync<BumpSpliceClientRequest, SpliceClientResponse>(
                           session, new BumpSpliceClientRequest(session.ChannelId, feeRatePerKw), ct);
        Console.WriteLine($"[nltg] bumpsplice at {feeRatePerKw} sat/kw: {response.State}, txid {response.SpliceTxId}, "
                        + $"capacity {response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary>Both <c>channel_reestablish</c> on the connection after <paramref name="from"/>.</summary>
    private static async Task AssertBothReestablishedAsync(SpliceWireRecorder wire, long from, CancellationToken ct)
    {
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.ChannelReestablish, from),
                            EclairChannelSession.UsableTimeout, "Eclair's channel_reestablish", ct);
        await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.ChannelReestablish, from),
                            EclairChannelSession.UsableTimeout, "our channel_reestablish", ct);
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
    /// The quiescence of a splice from <paramref name="from"/>: the initiator's <c>stfu</c> first, the other side's after
    /// it, both before <c>splice_init</c> (or <c>tx_init_rbf</c>) at <paramref name="initSequence"/>.
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
    /// The interactive part of a taproot splice from <paramref name="from"/>: a <c>commitment_signed</c> each way for the
    /// new funding before any <c>tx_signatures</c>, no <c>revoke_and_ack</c> or <c>tx_abort</c> in between, both
    /// <c>tx_signatures</c> for the splice txid with the MuSig2 <c>shared_input_partial_signature</c>; the transaction is
    /// in bitcoind's mempool and our negotiation is Signed. Returns the sequence after the last <c>tx_signatures</c>.
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
            Assert.True(sigs.HasSharedInputPartialSignature,
                        $"{(sigs.Inbound ? "Eclair's" : "our")} tx_signatures has no shared_input_partial_signature "
                      + $"(or an ECDSA one): {sigs.Hex}");
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
    /// The splice transaction spends the previous P2TR funding output by MuSig2 key path (one 64-byte witness element)
    /// and has exactly one P2TR output of <paramref name="expectedCapacitySat"/> (the new funding); with
    /// <paramref name="feeRatePerKw"/> (ours) its fee is that feerate for its weight within one vbyte (plus 2 WU per
    /// ECDSA signature of our wallet inputs), else at least 253 sat/kw.
    /// </summary>
    private async Task<Transaction> AssertSpliceTransactionAsync(ChannelSnapshot before, uint256 spliceTxId,
                                                                 ulong expectedCapacitySat, uint? feeRatePerKw,
                                                                 CancellationToken ct)
    {
        var rpc = _fixture.Bitcoin.Rpc;
        var tx = await rpc.GetRawTransactionAsync(spliceTxId, true, ct);
        var shared = Assert.Single(tx.Inputs, i => i.PrevOut == new OutPoint(before.FundingTxId,
                                                                               before.FundingOutputIndex));
        Assert.Equal(1, shared.WitScript.PushCount);
        Assert.Equal(64, shared.WitScript[0].Length);
        var funding = Assert.Single(tx.Outputs, o => o.ScriptPubKey.IsScriptType(ScriptType.Taproot)
                                                  && o.Value == Money.Satoshis((long)expectedCapacitySat));

        var fee = await GetFeeAsync(spliceTxId, ct);
        var verbose = await rpc.SendCommandAsync("getrawtransaction", ct, spliceTxId.ToString(), true);
        var weight = (ulong)verbose.Result["weight"]!;
        var ecdsaSignatures = tx.Inputs.SelectMany(i => i.WitScript.Pushes)
                                .Count(p => p.Length is >= 9 and <= 73 && p[0] == 0x30);
        Console.WriteLine($"[proof] taproot splice {spliceTxId}: funding output {tx.Outputs.IndexOf(funding)} of "
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
    /// are the splice, both sent <c>splice_locked</c> for it after <paramref name="from"/>, both list the new capacity
    /// and the same new short channel id, the funding output is P2TR and the channel is usable again.
    /// </summary>
    private async Task<LockedChannel> MineUntilLockedAsync(EclairChannelSession session, SpliceWireRecorder wire,
                                                           ChannelSnapshot before, uint256 spliceTxId,
                                                           ulong expectedCapacitySat, long from, CancellationToken ct)
    {
        for (var block = 1; ; block++)
        {
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            var eclair = await session.GetEclairChannelAsync(ct);
            var ours = await session.GetOurChannelAsync(ct);
            Console.WriteLine($"[proof] +{block} block(s): ours {ours.Describe()} funding "
                            + $"{(ours.FundingTxId is { } f ? new uint256((byte[])f) : null)}; eclair "
                            + $"{EclairChannelSession.DescribeEclair(eclair)} active {EclairJson.DescribeActive(eclair)}");
            if (EclairJson.ActiveCount(eclair) == 1 && EclairJson.FundingTxId(eclair) == spliceTxId.ToString()
             && ours.FundingTxId is { } funding && new uint256((byte[])funding) == spliceTxId
             && wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked, from) is not null)
                break;

            Assert.True(block < MaxLockBlocks, $"the splice was not locked by both ends in {MaxLockBlocks} blocks");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        var ourLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: false, MessageTypes.SpliceLocked, from),
                                            s_stepTimeout, "our splice_locked", ct);
        var theirLocked = await Poll.ForAsync(() => wire.FirstOrDefault(inbound: true, MessageTypes.SpliceLocked,
                                                                        from),
                                              s_stepTimeout, "Eclair's splice_locked", ct);
        Assert.Equal(spliceTxId, ourLocked.SpliceLockedTxId);
        Assert.Equal(spliceTxId, theirLocked.SpliceLockedTxId);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);

        var channel = await session.GetOurChannelAsync(ct);
        var eclairChannel = await session.GetEclairChannelAsync(ct);
        Console.WriteLine($"[proof] locked: ours {channel.Describe()}; eclair "
                        + $"{EclairChannelSession.DescribeEclair(eclairChannel)} active "
                        + EclairJson.DescribeActive(eclairChannel));
        Assert.Equal((long)expectedCapacitySat, channel.Capacity.Satoshi);
        Assert.Equal((long)expectedCapacitySat, EclairJson.Active(eclairChannel)!["fundingAmount"]!.GetValue<long>());
        Assert.NotNull(channel.ShortChannelId);
        var eclairScid = EclairJson.ShortChannelId(eclairChannel);
        Assert.NotNull(eclairScid);
        Assert.NotEqual(before.ShortChannelId, eclairScid);
        Assert.Equal(eclairScid, channel.ShortChannelId.Value.ToUInt64());
        Assert.Equal((long)expectedCapacitySat * 1_000,
                     (long)(channel.LocalBalance.MilliSatoshi + channel.RemoteBalance.MilliSatoshi));
        Assert.True(EclairTaprootTests.Model(session).ChannelParams.OptionSimpleTaproot);
        return new LockedChannel((long)channel.LocalBalance.MilliSatoshi,
                                 Math.Max(ourLocked.Sequence, theirLocked.Sequence));
    }

    /// <summary>
    /// While the splice was pending (between <paramref name="from"/> and <paramref name="to"/>) every
    /// <c>commitment_signed</c> of each side came in a <c>start_batch</c> of one per active funding in
    /// <paramref name="fundings"/>, answered by one <c>revoke_and_ack</c>.
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

    /// <summary>The channel before a splice, once Eclair lists its short channel id.</summary>
    private async Task<ChannelSnapshot> SnapshotAsync(EclairChannelSession session, CancellationToken ct)
    {
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var ours = await session.GetOurChannelAsync(ct);
        JsonNode? eclair = null;
        for (var block = 0; block < MaxLockBlocks; block++)
        {
            eclair = await session.GetEclairChannelAsync(ct);
            if (EclairJson.ShortChannelId(eclair) is not null)
                break;

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            eclair = await session.GetEclairChannelAsync(ct);
            if (EclairJson.ShortChannelId(eclair) is not null)
                break;

            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
        }

        Assert.NotNull(eclair);
        Assert.True(EclairJson.ShortChannelId(eclair) is not null,
                    $"Eclair lists no short channel id: {EclairJson.DescribeActive(eclair)}");
        Assert.NotNull(ours.FundingTxId);
        Assert.NotNull(ours.FundingOutputIndex);
        var funding = new uint256((byte[])ours.FundingTxId.Value);
        Assert.Equal(funding.ToString(), EclairJson.FundingTxId(eclair));
        Assert.Equal(1, EclairJson.ActiveCount(eclair));
        return new ChannelSnapshot(funding, ours.FundingOutputIndex.Value, (ulong)ours.Capacity.Satoshi,
                                   (long)ours.LocalBalance.MilliSatoshi, EclairJson.ShortChannelId(eclair)!.Value);
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