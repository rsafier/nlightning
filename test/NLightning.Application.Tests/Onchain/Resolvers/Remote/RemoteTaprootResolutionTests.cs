using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Application.Onchain.Resolvers.Remote;
using Channels.Services;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// NL-966 (T4) on a fake chain: the peer (Bob) force-closes a <b>simple taproot</b> channel with HTLCs in flight both
/// ways and every output of its commitment is resolved as on an anchors channel: our <c>to_remote</c> by its leaf, our
/// offered HTLC by the accepted output's timeout leaf after <c>cltv_expiry</c> (failed upstream at reasonable depth),
/// the peer's HTLC by the offered output's success leaf with an allowed preimage (our fulfill, or the switch's final-hop
/// acceptance), and a preimage the peer reveals in its taproot HTLC-success transaction fulfills upstream. Every claim
/// is executed by NBitcoin's interpreter against the P2TR output it spends; no <c>[NL-966]</c> alert is raised.
/// </summary>
public sealed class RemoteTaprootResolutionTests : IDisposable
{
    private const uint Cltv = 600;

    private static readonly ChannelId s_upstreamChannelId = new(Enumerable.Repeat((byte)0x71, 32).ToArray());

    private readonly RemoteResolutionTestContext _context = new(simpleTaproot: true);
    private RealSigningCommitmentPair? _upstream;

    private RealSigningCommitmentPair Pair => _context.Pair;

    [Fact]
    public async Task Given_PeersTaprootCommitmentWithHtlcsBothWays_When_Resolved_Then_EveryOutputClaimedByScriptPath()
    {
        // Arrange: our payment (HTLC we offered) and Bob's HTLC we fulfilled (persisted, never reached Bob)
        var ourPreimage = RealSigningCommitmentPair.Preimage(1);
        var ours = Pair.Add(Pair.Alice, 20_000_000, ourPreimage, Cltv);
        var theirPreimage = RealSigningCommitmentPair.Preimage(2);
        var theirs = Pair.Add(Pair.Bob, 30_000_000, theirPreimage, Cltv);
        Pair.Settle(Pair.Alice);
        Pair.Alice.Apply("fulfill", Pair.Alice.State.SendFulfill(theirs, theirPreimage, new Sha256()));
        _context.UseSnapshot();
        AddLocalPayment(ours, RealSigningCommitmentPair.Hash(ourPreimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: the first round
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: to_remote swept and Bob's HTLC claimed with the preimage at once; ours waits for its expiry
        var sweep = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.Sweep);
        Assert.True(_context.Verifies(sweep, out var error), error.ToString());
        var preimageClaim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.True(_context.Verifies(preimageClaim, out error), error.ToString());
        var preimageTx = Load(preimageClaim);
        Assert.Equal(4, preimageTx.Inputs[0].WitScript.PushCount);
        Assert.Equal((byte[])theirPreimage, preimageTx.Inputs[0].WitScript[1]);
        Assert.Equal(1U, preimageTx.Inputs[0].Sequence.Value);
        var ourRow = _context.SavedRows().Single(r => r is { HtlcDirection: HtlcDirection.Outgoing } && r.HtlcId == ours);
        Assert.Equal(OutputResolutionState.Waiting, ourRow.State);
        Assert.Equal(Cltv, ourRow.WaitUntilHeight);

        // Act: the tip reaches cltv_expiry
        await _context.ResolveAsync(Cltv);

        // Assert: <sig> <timeout leaf> <control block> at nLockTime cltv_expiry, nSequence 1
        var timeoutClaim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim
                                                               && b.TransactionId != preimageClaim.TransactionId);
        Assert.True(_context.Verifies(timeoutClaim, out error), error.ToString());
        var timeoutTx = Load(timeoutClaim);
        Assert.Equal(Cltv, timeoutTx.LockTime.Value);
        Assert.Equal(3, timeoutTx.Inputs[0].WitScript.PushCount);
        Assert.Equal(1U, timeoutTx.Inputs[0].Sequence.Value);

        // Act: everything confirms; the upstream (our payment) is failed once the timeout claim is reasonably deep
        await _context.MineAsync(sweep, Cltv + 1);
        await _context.MineAsync(preimageClaim, Cltv + 1);
        await _context.MineAsync(timeoutClaim, Cltv + 1);
        await _context.ResolveAsync(Cltv + 6);
        var final = await _context.ResolveAsync(Cltv + 101);

        // Assert
        Assert.NotEmpty(_context.SwitchEvents);
        Assert.All(_context.SwitchEvents, e =>
        {
            var failed = Assert.IsType<OutgoingHtlcFailed>(e);
            Assert.Equal(ours, failed.HtlcId);
            Assert.Equal(RemoteHtlcSwitchEvents.OnchainTimeoutKind, failed.Removal.Kind);
        });
        Assert.True(final.AllIrrevocablyResolved);
        Assert.DoesNotContain(_context.Alerts, a => a.RequirementId == "NL-966");
    }

    [Fact]
    public async Task Given_PeersTaprootHtlcSuccessRevealsThePreimage_When_Spent_Then_StagedAndFulfilledUpstream()
    {
        // Arrange: our HTLC forwards an upstream HTLC; Bob takes it with his taproot HTLC-success transaction
        var preimage = RealSigningCommitmentPair.Preimage(7);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        AddUpstreamForward(id, preimage);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var vout = _context.HtlcRow(id).OutputIndex;

        // <remotehtlcsig(65)> <localhtlcsig(64)> <preimage> <success leaf> <control block>
        var leaf = Enumerable.Repeat((byte)0x82, 70).ToArray();
        byte[] controlBlock = [0xc0, .. Enumerable.Repeat((byte)0x33, 64)];
        var spend = _context.PeerSpend(vout, Schnorr(65), Schnorr(64), preimage, leaf, controlBlock);

        // Act
        await _context.SpendAsync(vout, spend, 502);

        // Assert: the preimage is on our record before the fulfill, and the fulfill goes upstream
        Assert.Equal(preimage, _context.SavedHtlc(HtlcDirection.Outgoing, id)?.KnownPreimage);
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(_context.SwitchEvents[0]);
        Assert.Equal(id, fulfilled.HtlcId);
        Assert.Equal(preimage, fulfilled.PaymentPreimage);
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
    }

    [Fact]
    public async Task Given_ThePeersNextTaprootCommitmentOnChain_When_Resolved_Then_OurTimeoutClaimIsValid()
    {
        // Arrange (B5-RMT-01): Bob broadcasts the commitment we signed with our new HTLC before his revoke_and_ack
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(4), Cltv);
        Pair.Alice.Apply("commit", Pair.Alice.State.SendCommit(Pair.Alice.CommitmentSigner));
        var next = Pair.Alice.State.RemoteNextCommit;
        Assert.NotNull(next);
        _context.UseSnapshot();
        _context.CloseWith(next.Commit, ChannelCloseKind.RemoteNextCommitment);

        // Act
        await _context.BeginAsync(Cltv);

        // Assert
        Assert.Equal(OutputDescriptorKind.RemoteReceivedHtlc, _context.HtlcRow(id).Descriptor);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
    }

    [Fact]
    public async Task Given_APeerTaprootHtlcTheSwitchAcceptedAsFinalHop_When_Resolved_Then_ClaimedWithThePreimage()
    {
        // Arrange (NL-316/NL-322): Bob paid our invoice; the switch accepted the HTLC on chain
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, 30_000_000, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(RemoteFinalHopClaimTests.WithKnownPreimage(Pair.Alice.State, id, preimage));
        var invoice = new InvoiceModel(RealSigningCommitmentPair.Hash(preimage), preimage,
                                       new Secret(Enumerable.Repeat((byte)0x53, 32).ToArray()),
                                       LightningMoney.MilliSatoshis(30_000_000), "final hop", "lnbcrt-test",
                                       DateTimeOffset.UtcNow, 3_600, 18);
        invoice.Accept(LightningMoney.MilliSatoshis(30_000_000));
        invoice.Settle(DateTimeOffset.UtcNow);
        _context.Store.Invoices[invoice.PaymentHash] = invoice;
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal((byte[])preimage, Load(claim).Inputs[0].WitScript[1]);
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        Assert.Equal(OutputResolutionState.Broadcast, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_OurTaprootPreimageClaimUnconfirmed_When_TheRbfIntervalPassed_Then_ReSignedByItsLeafKeepingThePreimage()
    {
        // Arrange: our preimage claim of Bob's HTLC stays out of the blocks
        var preimage = RealSigningCommitmentPair.Preimage(2);
        var id = Pair.Add(Pair.Bob, 30_000_000, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        Pair.Alice.Apply("fulfill", Pair.Alice.State.SendFulfill(id, preimage, new Sha256()));
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        var policy = new Domain.Onchain.Fees.SweepFeePolicyOptions();
        var feeService = new Mock<Domain.Bitcoin.Interfaces.IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(RemoteResolutionTestContext.FeeratePerKw));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(RemoteResolutionTestContext.FeeratePerKw));
        var scheduler = new Application.Onchain.Fees.SweepScheduler(
            feeService.Object, Pair.Alice.Signer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Onchain.Fees.SweepScheduler>.Instance,
            new Domain.Onchain.Fees.SweepFeePolicy(policy));

        // Act
        var (unitOfWork, _) = _context.Store.CreateUnitOfWork();
        var actions = await scheduler.PlanAsync(_context.Close, _context.SavedRows(),
                                                RemoteResolutionTestContext.CloseHeight + policy.RbfIntervalBlocks,
                                                unitOfWork, TestContext.Current.CancellationToken);

        // Assert: a replacement of the same input, signed again by script path, the preimage kept, valid by execution
        var replacement = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(claim.TransactionId, replacement.ReplacesTransactionId);
        var tx = Load(replacement);
        Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal((byte[])preimage, tx.Inputs[0].WitScript[1]);
        Assert.NotEqual(Load(claim).Inputs[0].WitScript[0], tx.Inputs[0].WitScript[0]);
        Assert.True(_context.Verifies(replacement, out var error), error.ToString());
    }

    public void Dispose()
    {
        _context.Dispose();
        _upstream?.Dispose();
    }

    private static Transaction Load(BroadcastTransactionModel broadcast) =>
        Transaction.Load(broadcast.RawTransaction, Network.Main);

    private static byte[] Schnorr(int length) => Enumerable.Repeat((byte)0x31, length).ToArray();

    private void AddLocalPayment(ulong id, Hash paymentHash)
    {
        var payment = new PaymentModel(paymentHash, null, _context.Channel.RemoteNodeId, LightningMoney.Satoshis(20_000),
                                       LightningMoney.Zero, DateTimeOffset.UtcNow);
        _context.Store.Payments[paymentHash] = payment;
        _context.Store.Origins[(_context.Channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, id))] =
            HtlcOrigin.Local(paymentHash);
    }

    private void AddUpstreamForward(ulong outgoingId, Secret preimage)
    {
        _upstream = new RealSigningCommitmentPair(false);
        var upstreamId = _upstream.Add(_upstream.Bob, 21_000_000, preimage, Cltv + 40);
        _upstream.Settle(_upstream.Bob);
        _context.AddChannel(_upstream.Alice.Channel, s_upstreamChannelId, _upstream.Alice.State);
        _context.Store.Origins[(_context.Channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, outgoingId))] =
            HtlcOrigin.Forwarded(s_upstreamChannelId, upstreamId);
    }
}