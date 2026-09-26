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
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// BOLT 5 plan O4-T1..T3 on a fake chain (B5-RMT-* rows): the peer (Bob) force-closes with its current, next or a
/// future commitment, built from real engines and real signers; we (Alice) sweep <c>to_remote</c>, claim our offered
/// HTLCs after <c>cltv_expiry</c> and the peer's offered HTLCs with an allowed preimage before it, and fulfill or fail
/// the upstream HTLCs through the switch events. Every sweep and claim is checked by script execution against the
/// commitment output it spends. The resolver only returns actions; the context applies them as the executor does.
/// </summary>
public sealed class RemoteCommitResolverTests : IDisposable
{
    private const uint Cltv = 600;

    private static readonly ChannelId s_upstreamChannelId = new(Enumerable.Repeat((byte)0x71, 32).ToArray());
    private static readonly ChannelId s_downstreamChannelId = new(Enumerable.Repeat((byte)0x72, 32).ToArray());

    private RemoteResolutionTestContext _context = new();
    private RealSigningCommitmentPair? _upstream;

    private RealSigningCommitmentPair Pair => _context.Pair;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_PeerCommitmentWithOurBalance_When_Confirmed_Then_ToRemoteSweptAtOnceAndIrrevocableAt100(
        bool hasAnchors)
    {
        // Arrange (B5-RMT-02, D5): an idle channel with a push, Bob closes with his current commitment
        if (hasAnchors)
        {
            _context.Dispose();
            _context = new RemoteResolutionTestContext(true);
        }

        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: the commitment is classified at its own block (anchors: the CSV of 1 is already satisfied then)
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: one row, one watch, one sweep to the wallet with a valid witness, saved in the round's only save
        var row = _context.ToRemoteRow();
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        var sweep = Assert.Single(_context.Published);
        Assert.Equal(row.ResolvingTransactionId, sweep.TransactionId);
        Assert.Equal(BroadcastPurpose.Sweep, sweep.Purpose);
        Assert.True(_context.Store.Broadcasts.ContainsKey(sweep.TransactionId), "saved with the row");
        Assert.True(_context.Verifies(sweep, out var error), error.ToString());
        var tx = Transaction.Load(sweep.RawTransaction, Network.Main);
        Assert.Equal(RemoteResolutionTestContext.Destination, tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(hasAnchors ? 1U : SweepFeePolicy.RbfSequence, tx.Inputs[0].Sequence.Value);
        Assert.Contains(_context.Tracked, w => w.OutputIndex == row.OutputIndex);
        Assert.Empty(_context.Alerts);
        Assert.Empty(_context.SwitchEvents);

        // Act: a replayed block builds nothing again
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight);
        Assert.Single(_context.Published);

        // Act: our sweep confirms, then 99 and 100 blocks deep
        await _context.MineAsync(sweep, 501);
        Assert.Equal(OutputResolutionState.Resolved, _context.ToRemoteRow().State);
        Assert.False((await _context.ResolveAsync(599)).AllIrrevocablyResolved);
        var final = await _context.ResolveAsync(600);

        // Assert
        Assert.Equal(OutputResolutionState.Irrevocable, _context.ToRemoteRow().State);
        Assert.True(final.AllIrrevocablyResolved);
    }

    [Fact]
    public async Task Given_ARound_When_Resolved_Then_TheResolverNeverSavesAndTheDestinationIsAskedOncePerRound()
    {
        // Arrange (finding 5): to_remote and a timeout claim are both built in one round
        Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(Cltv);

        // Assert: two transactions, one destination, and the executor's save is the only one
        Assert.Equal(2, _context.Published.Count);
        Assert.Equal(1, _context.DestinationCalls);
        Assert.Equal(2, _context.Store.Saves); // the executor's per-block save and the round's save
    }

    [Fact]
    public async Task Given_OurOfferedHtlcOnPeerCommitment_When_TipReachesCltv_Then_TimeoutClaimValidAndFailedUpstreamAtDepth()
    {
        // Arrange (B5-RMT-LO-02): we offered an HTLC for our own payment, both sides committed, Bob closes
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: before cltv_expiry
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv - 1);

        // Assert: only to_remote swept; the HTLC waits for its expiry
        var htlcRow = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteReceivedHtlc, htlcRow.Descriptor);
        Assert.Equal(OutputResolutionState.Waiting, htlcRow.State);
        Assert.Equal(Cltv, htlcRow.WaitUntilHeight);
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);

        // Act: the tip reaches cltv_expiry
        await _context.ResolveAsync(Cltv);

        // Assert: <sig> <> with nLockTime = cltv_expiry, signed with our HTLC key at Bob's point
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        var tx = Transaction.Load(claim.RawTransaction, Network.Main);
        Assert.Equal(Cltv, tx.LockTime.Value);
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        Assert.Equal(2, tx.Inputs[0].WitScript.PushCount - 1);
        Assert.Empty(tx.Inputs[0].WitScript.Pushes.ElementAt(1));
        Assert.Equal(claim.TransactionId, _context.HtlcRow(id).ResolvingTransactionId);

        // Act: the claim confirms; the upstream is failed only once it is reasonably deep (6)
        await _context.MineAsync(claim, Cltv + 1);
        await _context.ResolveAsync(Cltv + 5);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(Cltv + 6);

        // Assert
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);
        Assert.Equal(RemoteHtlcSwitchEvents.OnchainTimeoutKind, failed.Removal.Kind);
        Assert.Equal(RealSigningCommitmentPair.Hash(preimage), failed.PaymentHash);
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_SwitchCouldNotApplyTheUpstreamFail_When_NextBlocks_Then_RaisedAgainUntilUpstreamResolved()
    {
        // Arrange (finding 2): the switch only logs a removal it cannot send (link down); nothing tells the resolver
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var payment = AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(Cltv);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        await _context.MineAsync(claim, Cltv + 1);
        await _context.ResolveAsync(Cltv + 6);
        Assert.Single(_context.SwitchEvents);

        // Act: the payment is still in flight on the next blocks, then it is failed
        await _context.ResolveAsync(Cltv + 7);
        await _context.ResolveAsync(Cltv + 8);
        var raisedWhileInFlight = _context.SwitchEvents.Count;
        _context.Store.Payments[payment.PaymentHash] = Failed(payment);
        await _context.ResolveAsync(Cltv + 9);

        // Assert: raised every block while the payment was in flight, never after
        Assert.Equal(3, raisedWhileInFlight);
        Assert.Equal(3, _context.SwitchEvents.Count);
        Assert.All(_context.SwitchEvents, e => Assert.Equal(id, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId));
    }

    [Fact]
    public async Task Given_PeerHtlcSuccessRevealsPreimage_When_Spent_Then_PreimageStagedInRecordAndFulfilledUntilUpstreamResolved()
    {
        // Arrange (B5-RMT-LO-01): our HTLC forwards an upstream HTLC; Bob claims it with its HTLC-success transaction
        var preimage = RealSigningCommitmentPair.Preimage(7);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var upstreamHtlcId = AddUpstreamForward(id, preimage);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var vout = _context.HtlcRow(id).OutputIndex;
        var spend = _context.PeerSpend(vout, _context.SuccessWitness(vout, preimage));

        // Act
        await _context.SpendAsync(vout, spend, 502);

        // Assert: the preimage is saved in our HTLC's record in the round that raised the fulfill, so the switch's own
        // replay (link-up, startup) derives the fulfill too
        var record = _context.SavedHtlc(HtlcDirection.Outgoing, id);
        Assert.NotNull(record);
        Assert.Equal(preimage, record.KnownPreimage);
        Assert.Contains(ChannelDomainEvents.DerivePending(_context.Channel.ChannelId, [record]),
                        e => e is OutgoingHtlcFulfilled f && f.PaymentPreimage == preimage);
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(_context.SwitchEvents[0]);
        Assert.Equal(id, fulfilled.HtlcId);
        Assert.Equal(preimage, fulfilled.PaymentPreimage);
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);

        // Act: the upstream fulfill could not be sent yet; the next blocks
        var before = _context.SwitchEvents.Count;
        await _context.ResolveAsync(503);
        Assert.Equal(before + 1, _context.SwitchEvents.Count);
        FulfillUpstream(upstreamHtlcId, preimage);
        await _context.ResolveAsync(504);

        // Assert: raised again while the upstream HTLC had no removal, never after; no timeout claim ever
        Assert.Equal(before + 1, _context.SwitchEvents.Count);
        Assert.All(_context.SwitchEvents, e => Assert.IsType<OutgoingHtlcFulfilled>(e));
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
    }

    [Fact]
    public async Task Given_WrongPreimageInWitness_When_Spent_Then_NotFulfilledAndLossAlerted()
    {
        // Arrange: a 32-byte item that does not hash to the payment hash is never used
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(7), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var vout = _context.HtlcRow(id).OutputIndex;
        var spend = _context.PeerSpend(vout, _context.SuccessWitness(vout, RealSigningCommitmentPair.Preimage(8)));

        // Act
        var round = await _context.SpendAsync(vout, spend, 502);

        // Assert
        Assert.DoesNotContain(_context.SwitchEvents, e => e is OutgoingHtlcFulfilled);
        Assert.Null(_context.SavedHtlc(HtlcDirection.Outgoing, id)?.KnownPreimage);
        Assert.Contains(round.Alerts, a => a.RequirementId == "B5-RMT-LO-02");
    }

    [Fact]
    public async Task Given_PeerOfferedHtlcWeFulfilled_When_PeerCommitmentConfirms_Then_PreimageClaimBeforeCltvValid()
    {
        // Arrange (B5-RMT-RO-01): Bob offered us an HTLC, locked in; our fulfill is persisted but never reached Bob
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, 30_000_000, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        Pair.Alice.Apply("fulfill", Pair.Alice.State.SendFulfill(id, preimage, new Sha256()));
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: <sig> <preimage> at nLockTime 0 (below cltv_expiry), deadline cltv_expiry
        var claim = AssertPreimageClaim(id, preimage);

        // Act: it confirms
        await _context.MineAsync(claim, 501);

        // Assert: resolved, and no upstream event for an HTLC the peer offered
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);
        Assert.Empty(_context.SwitchEvents);
    }

    [Fact]
    public async Task Given_ForwardFulfilledDownstreamAfterUpstreamClosed_When_NextBlock_Then_ClaimedWithTheDownstreamPreimage()
    {
        // Arrange (finding 1, B5-RMT-RO-01): Bob offered us an HTLC that we forwarded; Bob force-closes; the
        // downstream peer fulfills afterwards, when the switch can no longer fulfill upstream (channel not Open)
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, 30_000_000, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        using var downstream = new RealSigningCommitmentPair(false);
        var outgoingId = downstream.Add(downstream.Alice, 29_000_000, preimage, Cltv - 40);
        downstream.Settle(downstream.Alice);
        _context.AddChannel(downstream.Alice.Channel, s_downstreamChannelId, downstream.Alice.State);
        _context.Store.Origins[(s_downstreamChannelId, new HtlcKey(HtlcDirection.Outgoing, outgoingId))] =
            HtlcOrigin.Forwarded(_context.Channel.ChannelId, id);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: before the downstream fulfill nothing may be claimed
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        Assert.Equal(OutputResolutionState.Waiting, _context.HtlcRow(id).State);
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);

        // The downstream peer fulfills (the switch marks the circuit Fulfilled; the upstream fulfill is refused)
        downstream.Bob.Apply("fulfill", downstream.Bob.State.SendFulfill(outgoingId, preimage, new Sha256()));
        downstream.Alice.Apply("receive fulfill",
                               downstream.Alice.State.ReceiveFulfill(outgoingId, preimage, new Sha256()));
        _context.AddChannel(downstream.Alice.Channel, s_downstreamChannelId, downstream.Alice.State);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 1);

        // Assert
        AssertPreimageClaim(id, preimage);
    }

    [Fact]
    public async Task Given_ForwardFulfilledKnownOnlyThroughTheCircuit_When_Resolved_Then_ClaimedWithThePreimage()
    {
        // Arrange: no origin row points at our HTLC (e.g. a forward offered before NL-250); the circuit names it
        var preimage = RealSigningCommitmentPair.Preimage(3);
        var id = Pair.Add(Pair.Bob, 30_000_000, preimage, Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        using var downstream = new RealSigningCommitmentPair(false);
        var outgoingId = downstream.Add(downstream.Alice, 29_000_000, preimage, Cltv - 40);
        downstream.Settle(downstream.Alice);
        downstream.Bob.Apply("fulfill", downstream.Bob.State.SendFulfill(outgoingId, preimage, new Sha256()));
        downstream.Alice.Apply("receive fulfill",
                               downstream.Alice.State.ReceiveFulfill(outgoingId, preimage, new Sha256()));
        _context.AddChannel(downstream.Alice.Channel, s_downstreamChannelId, downstream.Alice.State);
        var circuit = NewCircuit(_context.Channel.ChannelId, id, RealSigningCommitmentPair.Hash(preimage));
        circuit.MarkFulfilled(s_downstreamChannelId, outgoingId, DateTimeOffset.UtcNow);
        _context.Store.Circuits.Add(circuit);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert
        AssertPreimageClaim(id, preimage);
    }

    [Fact]
    public async Task Given_PeerOfferedHtlcWithoutOurFulfill_When_PeerCommitmentConfirms_Then_NeverClaimedAndIgnoredAtExpiry()
    {
        // Arrange (B5-LCL-RO-02, B5-RMT-RO-02): no fulfill and no forward of ours, so no preimage may be used on chain
        var id = Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(3), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv - 1);
        Assert.Equal(OutputResolutionState.Waiting, _context.HtlcRow(id).State);
        await _context.ResolveAsync(Cltv);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(OutputResolutionState.Ignored, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_PeerNextCommitmentOnChain_When_Resolved_Then_MappedWithItsPointAndClaimsValid()
    {
        // Arrange (B5-RMT-01, slot 2): we signed Bob's next commitment with our new HTLC; Bob broadcasts it before its
        // revoke_and_ack
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(4), Cltv);
        Pair.Alice.Apply("commit", Pair.Alice.State.SendCommit(Pair.Alice.CommitmentSigner));
        var next = Pair.Alice.State.RemoteNextCommit;
        Assert.NotNull(next);
        Assert.DoesNotContain(Pair.Alice.State.RemoteCommit.Spec.Htlcs, h => h.Id == id);
        _context.UseSnapshot();
        _context.CloseWith(next.Commit, ChannelCloseKind.RemoteNextCommitment);

        // Act
        await _context.BeginAsync(Cltv);

        // Assert: the HTLC output is found in the next commitment and claimed with the key at its point
        var row = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteReceivedHtlc, row.Descriptor);
        Assert.Equal(next.Commit.PerCommitmentPoint, OutputDescriptorData.Decode(row.DescriptorData).PerCommitmentPoint);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        var sweep = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.Sweep);
        Assert.True(_context.Verifies(sweep, out error), error.ToString());
    }

    [Fact]
    public async Task Given_FutureCommitmentAfterDataLoss_When_Resolved_Then_EveryOutputWatchedToRemoteSweptAndCriticalAlert()
    {
        // Arrange (B5-RMT-03): our snapshot is an old backup; Bob closes with a newer commitment holding HTLCs
        var old = Pair.Alice.State;
        Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(5), Cltv);
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Alice);
        var future = Pair.Alice.State.RemoteCommit;
        Assert.True(future.Number > old.RemoteCommit.Number);
        _context.UseSnapshot(old);
        var commitment = _context.CloseWith(future, ChannelCloseKind.FutureCommitment);

        // Act
        var round = await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: to_remote (static_remotekey: no point needed) swept with a valid witness; every other output watched
        Assert.Contains(round.Alerts, a => a.RequirementId == "B5-RMT-03");
        Assert.Equal(commitment.Outputs.Count, _context.SavedRows().Count);
        Assert.Equal(commitment.Outputs.Count, _context.Tracked.Count);
        var toRemote = _context.ToRemoteRow();
        Assert.All(_context.SavedRows().Where(r => r.OutputIndex != toRemote.OutputIndex),
                   r => Assert.Equal(OutputDescriptorKind.Unknown, r.Descriptor));
        var sweep = Assert.Single(_context.Published);
        Assert.True(_context.Verifies(sweep, out var error), error.ToString());
        Assert.Empty(_context.SwitchEvents);

        // Act: the next block raises the alert no more
        var later = await _context.ResolveAsync(501);
        Assert.Empty(later.Alerts);
    }

    [Fact]
    public async Task Given_FutureCommitment_When_PeerSpendRevealsOurHtlcPreimage_Then_FulfilledAndNotIgnoredMeanwhile()
    {
        // Arrange (finding 4, B5-RMT-LO-01 after data loss): our backup still holds our offered HTLC; Bob closes with
        // a newer commitment that has it and claims it with its HTLC-success transaction before it expires (NL-320:
        // past cltv_expiry + 6 without a preimage it would be failed upstream)
        var preimage = RealSigningCommitmentPair.Preimage(5);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv + 100);
        Pair.Settle(Pair.Alice);
        var backup = Pair.Alice.State;
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Bob);
        var future = Pair.Alice.State.RemoteCommit;
        _context.UseSnapshot(backup);
        AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(future, ChannelCloseKind.FutureCommitment);
        var vout = _context.Mapper.Map(_context.Channel, CommitmentTxSpec.FromCommitmentSpec(future.Spec),
                                       CommitmentCase.Remote, future.Number, future.PerCommitmentPoint)
                           .Outputs.Single(o => o.Htlc is { Direction: HtlcDirection.Outgoing } h && h.Id == id)
                           .Vout;
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Act: 100 blocks deep nothing is ignored while our HTLC is open and not expired long ago
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 150);
        Assert.Equal(OutputResolutionState.Pending, _context.Row(vout).State);
        var spend = _context.PeerSpend(vout, _context.SuccessWitness(vout, preimage));
        await _context.SpendAsync(vout, spend, RemoteResolutionTestContext.CloseHeight + 151);

        // Assert: fulfilled at the spend, and again by the block's round from the staged record (NL-320: raised every
        // round until the upstream has its removal), never failed
        Assert.NotEmpty(_context.SwitchEvents);
        Assert.All(_context.SwitchEvents, e =>
        {
            var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(e);
            Assert.Equal(id, fulfilled.HtlcId);
            Assert.Equal(preimage, fulfilled.PaymentPreimage);
        });
        Assert.Equal(preimage, _context.SavedHtlc(HtlcDirection.Outgoing, id)?.KnownPreimage);
    }

    [Fact]
    public async Task Given_FutureCommitmentWithExpiredHtlcs_When_LongAfter_Then_UnspentUnknownOutputsIgnored()
    {
        // Arrange: as above, but no spend ever reveals anything
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(5), Cltv);
        Pair.Settle(Pair.Alice);
        var backup = Pair.Alice.State;
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(backup);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.FutureCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        Assert.NotNull(_context.SavedHtlc(HtlcDirection.Outgoing, id));

        // Act: cltv_expiry + 99, then + 100
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.IrrevocableDepth - 1);
        Assert.All(_context.SavedRows().Where(r => r.Descriptor == OutputDescriptorKind.Unknown),
                   r => Assert.Equal(OutputResolutionState.Pending, r.State));
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.IrrevocableDepth);

        // Assert
        Assert.All(_context.SavedRows().Where(r => r.Descriptor == OutputDescriptorKind.Unknown),
                   r => Assert.Equal(OutputResolutionState.Ignored, r.State));
    }

    [Fact]
    public async Task Given_FutureCommitmentWithOurForwardedHtlc_When_ExpiredReasonablyDeep_Then_FailedUpstreamEveryRoundBeforeClosed()
    {
        // Arrange (NL-320): our backup holds our offered HTLC, which forwards an HTLC of an open upstream channel; Bob
        // closes with a newer commitment we cannot rebuild, and nothing ever reveals the preimage
        var preimage = RealSigningCommitmentPair.Preimage(5);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        var backup = Pair.Alice.State;
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(backup);
        AddUpstreamForward(id, preimage);
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.FutureCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Act: until cltv_expiry + 5 nothing is failed
        await _context.ResolveAsync(Cltv);
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth - 1);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth);

        // Assert: our own permanent_channel_failure upstream (OnchainTimeout), with every unknown output still watched
        // (the channel cannot close before the upstream is resolved, which the old path only did at cltv_expiry + 100)
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);
        Assert.Equal(RemoteHtlcSwitchEvents.OnchainTimeoutKind, failed.Removal.Kind);
        Assert.Equal(RealSigningCommitmentPair.Hash(preimage), failed.PaymentHash);
        Assert.All(_context.SavedRows().Where(r => r.Descriptor == OutputDescriptorKind.Unknown),
                   r => Assert.Equal(OutputResolutionState.Pending, r.State));

        // Act: the switch could not send the fail yet (the upstream HTLC has no removal): raised again
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth + 1);

        // Assert
        Assert.Equal(2, _context.SwitchEvents.Count);
        Assert.All(_context.SwitchEvents, e => Assert.Equal(id, Assert.IsType<OutgoingHtlcFailed>(e).HtlcId));
    }

    [Fact]
    public async Task Given_FutureCommitmentWithOurPayment_When_FailedUpstream_Then_NoMoreEventsAndOutputsIgnoredLongBeforeExpiryPlus100()
    {
        // Arrange (NL-320)
        var preimage = RealSigningCommitmentPair.Preimage(5);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        var backup = Pair.Alice.State;
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(backup);
        var payment = AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.FutureCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth);
        Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));

        // Act: the payment took the failure
        _context.Store.Payments[payment.PaymentHash] = Failed(payment);
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth + 1);

        // Assert: nothing raised again, and the unknown outputs (the commitment is 100 deep) are ignored now
        Assert.Single(_context.SwitchEvents);
        Assert.All(_context.SavedRows().Where(r => r.Descriptor == OutputDescriptorKind.Unknown),
                   r => Assert.Equal(OutputResolutionState.Ignored, r.State));
    }

    [Fact]
    public async Task Given_FutureCommitmentConfirmedAfterOurHtlcExpired_When_NotReasonablyDeep_Then_NotFailedYet()
    {
        // Arrange (NL-320): the HTLC expired before the close; the upstream fail waits for the close to be 6 deep
        const uint expired = RemoteResolutionTestContext.CloseHeight - 100;
        var preimage = RealSigningCommitmentPair.Preimage(5);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, expired);
        Pair.Settle(Pair.Alice);
        var backup = Pair.Alice.State;
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(backup);
        AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.FutureCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + RemoteResolutionTestContext.ReasonableDepth
                                  - 2);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + RemoteResolutionTestContext.ReasonableDepth
                                  - 1);

        // Assert
        Assert.Equal(id, Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents)).HtlcId);
    }

    [Fact]
    public async Task Given_FutureCommitment_When_PreimageKnownOffChain_Then_FulfilledUpstreamNeverFailed()
    {
        // Arrange (NL-320): Bob fulfilled our HTLC off chain (the preimage is on our record), then closed with a
        // commitment newer than our backup
        var preimage = RealSigningCommitmentPair.Preimage(5);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        var state = Pair.Alice.State;
        var record = state.GetHtlc(HtlcDirection.Outgoing, id)! with { KnownPreimage = preimage };
        var backup = ChannelCommitments.Restore(state.ChannelId, state.Params, state.LocalBalanceMsat,
                                                state.RemoteBalanceMsat, state.Htlcs.SetItem(record.Key, record).Values,
                                                state.FeeUpdates, state.LocalNextHtlcId, state.RemoteNextHtlcId,
                                                state.LocalCommit, state.RemoteCommit, state.RemoteNextCommit,
                                                state.RemoteNextPerCommitmentPoint);
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot(backup);
        AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.FutureCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth);

        // Assert
        Assert.NotEmpty(_context.SwitchEvents);
        Assert.All(_context.SwitchEvents,
                   e => Assert.Equal(preimage, Assert.IsType<OutgoingHtlcFulfilled>(e).PaymentPreimage));
    }

    [Fact]
    public async Task Given_UnknownFundingSpend_When_Resolved_Then_EveryOutputWatchedAndOurHtlcFailedUpstreamAtDepth()
    {
        // Arrange (NL-320, B5-GEN-06): the funding output was spent by a transaction that is no known commitment
        var preimage = RealSigningCommitmentPair.Preimage(5);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        var spend = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.Unknown);

        // Act
        var round = await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: every output watched, the alert raised once
        Assert.Contains(round.Alerts, a => a.RequirementId == "B5-GEN-06");
        Assert.Equal(spend.Outputs.Count, _context.SavedRows().Count);
        Assert.Equal(spend.Outputs.Count, _context.Tracked.Count);

        // Act: past cltv_expiry + reasonable depth
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth - 1);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(Cltv + RemoteResolutionTestContext.ReasonableDepth);

        // Assert
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);
        Assert.Equal(RemoteHtlcSwitchEvents.OnchainTimeoutKind, failed.Removal.Kind);
    }

    [Fact]
    public async Task Given_TrimmedOfferedHtlc_When_PeerCommitmentConfirms_Then_FailedUpstreamAtOnceUntilThePaymentFails()
    {
        // Arrange (B5-RMT-LO-03): a 1000 sat HTLC is trimmed in every commitment
        var preimage = RealSigningCommitmentPair.Preimage(9);
        var id = Pair.Add(Pair.Alice, 1_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var payment = AddLocalPayment(id, RealSigningCommitmentPair.Hash(preimage));
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert: no row for it; failed upstream in the first round (no valid commitment has an output for it)
        Assert.DoesNotContain(_context.Store.Outputs.Values, o => o.HtlcId == id);
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);

        // Act: next block: raised again while the payment is in flight, then never again
        await _context.ResolveAsync(501);
        Assert.Equal(2, _context.SwitchEvents.Count);
        _context.Store.Payments[payment.PaymentHash] = Failed(payment);
        await _context.ResolveAsync(502);
        Assert.Equal(2, _context.SwitchEvents.Count);
    }

    [Fact]
    public async Task Given_TrimmedOfferedHtlcFulfilledOffChain_When_PeerCommitmentConfirms_Then_FulfilledUpstream()
    {
        // Arrange: Bob fulfilled it off chain before closing (we know the preimage)
        var preimage = RealSigningCommitmentPair.Preimage(9);
        var id = Pair.Add(Pair.Alice, 1_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        Pair.Alice.Apply("receive fulfill", Pair.Alice.State.ReceiveFulfill(id, preimage, new Sha256()));
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, fulfilled.HtlcId);
        Assert.Equal(preimage, fulfilled.PaymentPreimage);
    }

    [Fact]
    public async Task Given_HtlcOnlyInPeerNextCommitment_When_CurrentConfirms_Then_FailedUpstreamAtReasonableDepth()
    {
        // Arrange: our new HTLC is in Bob's next commitment (and none else); Bob broadcasts its current one
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(4), Cltv);
        Pair.Alice.Apply("commit", Pair.Alice.State.SendCommit(Pair.Alice.CommitmentSigner));
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: 5 blocks deep, then 6
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 4);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 5);

        // Assert
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);
    }

    [Fact]
    public async Task Given_TimeoutClaimAbandonedAsUneconomic_When_PeerLaterClaimsWithPreimage_Then_FulfilledUpstream()
    {
        // Arrange (finding 6): a fee floor so high that no output pays its own sweep; our HTLC's claim is abandoned
        _context.FeePolicy = new SweepFeePolicy(new SweepFeePolicyOptions { MinFeeratePerKw = 200_000 });
        var preimage = RealSigningCommitmentPair.Preimage(7);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(Cltv);
        Assert.Equal(OutputResolutionState.Ignored, _context.HtlcRow(id).State);
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);

        // Act: the peer claims it with the preimage after all
        var vout = _context.HtlcRow(id).OutputIndex;
        await _context.SpendAsync(vout, _context.PeerSpend(vout, _context.SuccessWitness(vout, preimage)), Cltv + 1);

        // Assert
        Assert.Contains(_context.SwitchEvents, e => e is OutgoingHtlcFulfilled f && f.HtlcId == id);
        Assert.DoesNotContain(_context.SwitchEvents, e => e is OutgoingHtlcFailed);
    }

    [Fact]
    public async Task Given_TimeoutClaimAbandonedAsUneconomic_When_NeverSpent_Then_FailedUpstreamAtReasonableDepth()
    {
        // Arrange (finding 6): as above, but the output stays unspent: the HTLC is as good as trimmed
        _context.FeePolicy = new SweepFeePolicy(new SweepFeePolicyOptions { MinFeeratePerKw = 200_000 });
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(7), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(Cltv);
        Assert.Equal(OutputResolutionState.Ignored, _context.HtlcRow(id).State);

        // Act: the commitment (confirmed at 500) is far deeper than 6 blocks at the expiry
        await _context.ResolveAsync(Cltv + 1);

        // Assert
        Assert.Contains(_context.SwitchEvents, e => e is OutgoingHtlcFailed f && f.HtlcId == id);
    }

    [Fact]
    public async Task Given_FirstRoundReplayed_When_SameCommitment_Then_NoDuplicateRowsWatchesOrBroadcasts()
    {
        // Arrange
        Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var rows = _context.Store.Outputs.Count;
        var watches = _context.Tracked.Count;

        // Act
        var replay = await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);

        // Assert
        Assert.Equal(rows, _context.Store.Outputs.Count);
        Assert.Equal(2, rows);
        Assert.Equal(watches, _context.Tracked.Count);
        Assert.Empty(replay.Broadcasts);
        Assert.Single(_context.Published);
    }

    [Fact]
    public async Task Given_ReplayedSpendOfOurSweep_When_SpentAgain_Then_NothingChanges()
    {
        // Arrange
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var sweep = Assert.Single(_context.Published);
        await _context.MineAsync(sweep, 501);
        var row = _context.ToRemoteRow();

        // Act
        var replay = await _context.MineAsync(sweep, 501);

        // Assert
        Assert.Empty(replay.Actions);
        Assert.Equal(row, _context.ToRemoteRow());
        Assert.Empty(_context.Alerts);
    }

    [Fact]
    public async Task Given_PeerTakesOurToRemote_When_Spent_Then_LossAlertedOnce()
    {
        // Arrange
        _context.UseSnapshot();
        _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(RemoteResolutionTestContext.CloseHeight);
        var vout = _context.ToRemoteRow().OutputIndex;

        // Act
        await _context.SpendAsync(vout, _context.PeerSpend(vout, RemoteResolutionTestContext.FakeSignature()), 501);
        await _context.ResolveAsync(502);

        // Assert
        Assert.Single(_context.Alerts, a => a.RequirementId == "B5-RMT-02");
    }

    [Fact]
    public void Given_CloseOfAnotherKind_When_Checked_Then_NotResolvedHere()
    {
        // Arrange
        var resolver = _context.CreateResolver();

        // Act / Assert
        Assert.True(resolver.CanResolve(ChannelCloseKind.RemoteCommitment));
        Assert.True(resolver.CanResolve(ChannelCloseKind.RemoteNextCommitment));
        Assert.True(resolver.CanResolve(ChannelCloseKind.FutureCommitment));
        Assert.True(resolver.CanResolve(ChannelCloseKind.Unknown));
        Assert.False(resolver.CanResolve(ChannelCloseKind.LocalCommitment));
        Assert.False(resolver.CanResolve(ChannelCloseKind.RevokedCommitment));
        Assert.False(resolver.CanResolve(ChannelCloseKind.Mutual));
    }

    public void Dispose()
    {
        _context.Dispose();
        _upstream?.Dispose();
    }

    /// <summary><paramref name="id"/> pays our own in-flight payment for <paramref name="paymentHash"/>.</summary>
    private PaymentModel AddLocalPayment(ulong id, Hash paymentHash)
    {
        var payment = new PaymentModel(paymentHash, null, _context.Channel.RemoteNodeId, LightningMoney.Satoshis(20_000),
                                       LightningMoney.Zero, DateTimeOffset.UtcNow);
        _context.Store.Payments[paymentHash] = payment;
        _context.Store.Origins[(_context.Channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, id))] =
            HtlcOrigin.Local(paymentHash);
        return payment;
    }

    private static PaymentModel Failed(PaymentModel payment) =>
        PaymentModel.Restore(payment.PaymentHash, payment.Bolt11, payment.PayeeNodeId, payment.Amount, payment.Fee,
                             payment.CreatedAt, PaymentStatus.Failed, null, null, null, null, null, "on-chain timeout",
                             DateTimeOffset.UtcNow);

    /// <summary>
    /// Our HTLC <paramref name="outgoingId"/> forwards an HTLC the peer of an open upstream channel offered us (no
    /// removal yet). Returns the upstream HTLC id.
    /// </summary>
    private ulong AddUpstreamForward(ulong outgoingId, Secret preimage)
    {
        _upstream = new RealSigningCommitmentPair(false);
        var upstreamId = _upstream.Add(_upstream.Bob, 21_000_000, preimage, Cltv + 40);
        _upstream.Settle(_upstream.Bob);
        _context.AddChannel(_upstream.Alice.Channel, s_upstreamChannelId, _upstream.Alice.State);
        _context.Store.Origins[(_context.Channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, outgoingId))] =
            HtlcOrigin.Forwarded(s_upstreamChannelId, upstreamId);
        return upstreamId;
    }

    /// <summary>The switch's upstream fulfill went through: the upstream HTLC has its removal.</summary>
    private void FulfillUpstream(ulong upstreamId, Secret preimage)
    {
        var upstream = _upstream ?? throw new InvalidOperationException("No upstream channel");
        upstream.Alice.Apply("fulfill", upstream.Alice.State.SendFulfill(upstreamId, preimage, new Sha256()));
        _context.AddChannel(upstream.Alice.Channel, s_upstreamChannelId, upstream.Alice.State);
    }

    private static ForwardCircuitModel NewCircuit(ChannelId incomingChannelId, ulong incomingHtlcId, Hash paymentHash) =>
        new(incomingChannelId, incomingHtlcId, LightningMoney.MilliSatoshis(30_000_000), Cltv, paymentHash,
            new Secret(new byte[32]), new ShortChannelId(1, 2, 3), LightningMoney.MilliSatoshis(29_000_000), Cltv - 40,
            DateTimeOffset.UtcNow);

    /// <summary>Our preimage claim of the peer's HTLC <paramref name="id"/>: published, valid, before the expiry.
    /// </summary>
    private BroadcastTransactionModel AssertPreimageClaim(ulong id, Secret preimage)
    {
        var row = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteOfferedHtlc, row.Descriptor);
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Equal(Cltv, row.DeadlineHeight);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(row.ResolvingTransactionId, claim.TransactionId);
        var tx = Transaction.Load(claim.RawTransaction, Network.Main);
        Assert.Equal(0U, tx.LockTime.Value);
        Assert.Equal((byte[])preimage, tx.Inputs[0].WitScript.Pushes.ElementAt(1));
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        return claim;
    }
}