using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Application.Onchain.Resolvers.Remote;
using Channels.Services;
using Domain.Channels.Commitments.Events;
using Domain.Onchain.Enums;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// BOLT 5 plan O4-T1..T3 on a fake chain (B5-RMT-* rows): the peer (Bob) force-closes with its current, next or a
/// future commitment, built from real engines and real signers; we (Alice) sweep <c>to_remote</c>, claim our offered
/// HTLCs after <c>cltv_expiry</c> and the peer's offered HTLCs with our preimage before it, and fulfill or fail the
/// upstream HTLCs through the switch events. Every sweep and claim is checked by script execution against the
/// commitment output it spends.
/// </summary>
public sealed class RemoteCommitResolverTests : IDisposable
{
    private const uint Cltv = 600;

    private RemoteResolutionTestContext _context = new();

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
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: the commitment is classified at its own block (anchors: the CSV of 1 is already satisfied then)
        var round = await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);

        // Assert: one row, one watch, one sweep to the wallet with a valid witness, published after the save
        var row = _context.ToRemoteRow();
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        var sweep = Assert.Single(_context.Published);
        Assert.Equal(row.ResolvingTransactionId, sweep.TransactionId);
        Assert.Equal(BroadcastPurpose.Sweep, sweep.Purpose);
        Assert.True(_context.Store.Broadcasts.ContainsKey(sweep.TransactionId), "saved before the publish");
        Assert.True(_context.Verifies(sweep, out var error), error.ToString());
        var tx = Transaction.Load(sweep.RawTransaction, Network.Main);
        Assert.Equal(RemoteResolutionTestContext.Destination, tx.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(hasAnchors ? 1U : SweepFeePolicyRbfSequence, tx.Inputs[0].Sequence.Value);
        Assert.Contains(_context.Tracked, w => w.OutputIndex == row.OutputIndex);
        Assert.False(round.CriticalDataLoss);
        Assert.Empty(_context.SwitchEvents);

        // Act: a replayed block changes nothing
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
    public async Task Given_OurOfferedHtlcOnPeerCommitment_When_TipReachesCltv_Then_TimeoutClaimValidAndFailedUpstreamAtDepth()
    {
        // Arrange (B5-RMT-LO-02): we offered an HTLC, both sides committed, Bob closes
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: before cltv_expiry
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
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

        // Act: the claim confirms; the upstream is failed only once it is reasonably deep (6), and only once
        await _context.MineAsync(claim, Cltv + 1);
        await _context.ResolveAsync(Cltv + 5);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(Cltv + 6);
        await _context.ResolveAsync(Cltv + 7);

        // Assert
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);
        Assert.Equal(RemoteHtlcSwitchEvents.OnchainTimeoutKind, failed.Removal.Kind);
        Assert.Equal(RealSigningCommitmentPair.Hash(RealSigningCommitmentPair.Preimage(1)), failed.PaymentHash);
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);
    }

    [Fact]
    public async Task Given_UpstreamEventSavedButProcessRestarted_When_NextBlock_Then_RaisedOnceMore()
    {
        // Arrange: as above, up to the upstream failure
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(commitment, Cltv);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        await _context.MineAsync(claim, Cltv + 1);
        await _context.ResolveAsync(Cltv + 6);
        Assert.Single(_context.SwitchEvents);

        // Act: a crash may have hit between the save and the raise; after the restart the event goes out once more
        _context.Restart();
        await _context.ResolveAsync(Cltv + 7);
        await _context.ResolveAsync(Cltv + 8);

        // Assert
        Assert.Equal(2, _context.SwitchEvents.Count);
        Assert.All(_context.SwitchEvents, e => Assert.Equal(id, e.HtlcId));
    }

    [Fact]
    public async Task Given_PeerHtlcSuccessRevealsPreimage_When_Spent_Then_PreimageSavedAndFulfilledUpstreamAtOnce()
    {
        // Arrange (B5-RMT-LO-01): our offered HTLC; Bob claims it with its HTLC-success transaction
        var preimage = RealSigningCommitmentPair.Preimage(7);
        var id = Pair.Add(Pair.Alice, 20_000_000, preimage, Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
        var vout = _context.HtlcRow(id).OutputIndex;
        var witnessScript = _context.CommitmentTx.Outputs[(int)vout].ScriptPubKey; // not checked by the resolver
        var spend = _context.PeerSpend(vout, [], RemoteResolutionTestContext.FakeSignature(),
                                       RemoteResolutionTestContext.FakeSignature(), preimage,
                                       witnessScript.ToBytes());

        // Act
        await _context.SpendAsync(vout, spend, 502);

        // Assert: the preimage is in the saved row, the fulfill went to the switch, the output is resolved
        var data = RemoteOutputData.Decode(_context.HtlcRow(id).DescriptorData);
        Assert.NotNull(data.Spend);
        Assert.False(data.Spend.ByUs);
        Assert.Equal(HtlcSpendPath.HtlcSuccessTransaction, data.Spend.Path);
        Assert.Equal((byte[])preimage, data.Spend.Preimage);
        var fulfilled = Assert.IsType<OutgoingHtlcFulfilled>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, fulfilled.HtlcId);
        Assert.Equal(preimage, fulfilled.PaymentPreimage);
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);

        // Act: later blocks, even past the expiry: no timeout claim, no second event
        await _context.ResolveAsync(Cltv + 10);
        Assert.Single(_context.SwitchEvents);
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
    }

    [Fact]
    public async Task Given_WrongPreimageInWitness_When_Spent_Then_NotFulfilledAndLossAlerted()
    {
        // Arrange: a 32-byte item that does not hash to the payment hash is never used
        var id = Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(7), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
        var vout = _context.HtlcRow(id).OutputIndex;
        var spend = _context.PeerSpend(vout, [], RemoteResolutionTestContext.FakeSignature(),
                                       RemoteResolutionTestContext.FakeSignature(),
                                       RealSigningCommitmentPair.Preimage(8), [0x51]);

        // Act
        var round = await _context.SpendAsync(vout, spend, 502);

        // Assert
        Assert.DoesNotContain(_context.SwitchEvents, e => e is OutgoingHtlcFulfilled);
        Assert.Contains(round.Alerts, a => a.StartsWith("B5-RMT-LO-02"));
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
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);

        // Assert: <sig> <preimage> at nLockTime 0 (below cltv_expiry), deadline cltv_expiry
        var row = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteOfferedHtlc, row.Descriptor);
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Equal(Cltv, row.DeadlineHeight);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        var tx = Transaction.Load(claim.RawTransaction, Network.Main);
        Assert.Equal(0U, tx.LockTime.Value);
        Assert.Equal((byte[])preimage, tx.Inputs[0].WitScript.Pushes.ElementAt(1));
        Assert.True(_context.Verifies(claim, out var error), error.ToString());

        // Act: it confirms
        await _context.MineAsync(claim, 501);

        // Assert: resolved, and no upstream event for an HTLC the peer offered
        Assert.Equal(OutputResolutionState.Resolved, _context.HtlcRow(id).State);
        Assert.Empty(_context.SwitchEvents);
    }

    [Fact]
    public async Task Given_PeerOfferedHtlcWithoutOurFulfill_When_PeerCommitmentConfirms_Then_NeverClaimedAndIrrevocableAtExpiry()
    {
        // Arrange (B5-LCL-RO-02, B5-RMT-RO-02): no fulfill of ours, so no preimage may be used on chain
        var id = Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(3), Cltv);
        Pair.Settle(Pair.Bob);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(Cltv - 1);
        Assert.Equal(OutputResolutionState.Waiting, _context.HtlcRow(id).State);
        await _context.ResolveAsync(Cltv);

        // Assert
        Assert.DoesNotContain(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.Equal(OutputResolutionState.Irrevocable, _context.HtlcRow(id).State);
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
        var commitment = _context.CloseWith(next.Commit, ChannelCloseKind.RemoteNextCommitment);

        // Act
        await _context.BeginAsync(commitment, Cltv);

        // Assert: the HTLC output is found in the next commitment and claimed with the key at its point
        var row = _context.HtlcRow(id);
        Assert.Equal(OutputDescriptorKind.RemoteReceivedHtlc, row.Descriptor);
        Assert.Equal(next.Commit.PerCommitmentPoint,
                     RemoteOutputData.Decode(row.DescriptorData).RemotePerCommitmentPoint);
        var claim = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.HtlcClaim);
        Assert.True(_context.Verifies(claim, out var error), error.ToString());
        var sweep = Assert.Single(_context.Published, b => b.Purpose == BroadcastPurpose.Sweep);
        Assert.True(_context.Verifies(sweep, out error), error.ToString());
    }

    [Fact]
    public async Task Given_FutureCommitmentAfterDataLoss_When_Resolved_Then_OnlyToRemoteSweptAndCriticalAlert()
    {
        // Arrange (B5-RMT-03): our snapshot is an old backup; Bob closes with a newer commitment holding HTLCs
        _context.UseSnapshot(Pair.Alice.State);
        var old = Pair.Alice.State;
        Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(5), Cltv);
        Pair.Add(Pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(6), Cltv);
        Pair.Settle(Pair.Alice);
        var future = Pair.Alice.State.RemoteCommit;
        Assert.True(future.Number > old.RemoteCommit.Number);
        _context.UseSnapshot(old);
        var commitment = _context.CloseWith(future, ChannelCloseKind.FutureCommitment);

        // Act
        var round = await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);

        // Assert: to_remote (static_remotekey: no point needed) swept with a valid witness; nothing else touched
        Assert.True(round.CriticalDataLoss);
        Assert.True(_context.Memory.CriticalAlerts.ContainsKey(_context.Channel.ChannelId));
        var row = Assert.Single(_context.Store.Outputs.Values);
        Assert.Equal(OutputDescriptorKind.PaymentToRemote, row.Descriptor);
        var sweep = Assert.Single(_context.Published);
        Assert.True(_context.Verifies(sweep, out var error), error.ToString());
        Assert.Empty(_context.SwitchEvents);

        // Act: a later round (after a restart) raises the alert again
        _context.Restart();
        var later = await _context.ResolveAsync(501);
        Assert.True(later.CriticalDataLoss);
        Assert.True(_context.Memory.CriticalAlerts.ContainsKey(_context.Channel.ChannelId));
    }

    [Fact]
    public async Task Given_TrimmedOfferedHtlc_When_PeerCommitmentConfirms_Then_FailedUpstreamAtOnce()
    {
        // Arrange (B5-RMT-LO-03): a 1000 sat HTLC is trimmed in every commitment
        var id = Pair.Add(Pair.Alice, 1_000_000, RealSigningCommitmentPair.Preimage(9), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);

        // Assert: no row for it; failed upstream in the first round (no valid commitment has an output for it)
        Assert.DoesNotContain(_context.Store.Outputs.Values, o => o.HtlcId == id);
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);

        // Act: next block: not raised again
        await _context.ResolveAsync(501);
        Assert.Single(_context.SwitchEvents);
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
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);

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
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);

        // Act: 5 blocks deep, then 6
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 4);
        Assert.Empty(_context.SwitchEvents);
        await _context.ResolveAsync(RemoteResolutionTestContext.CloseHeight + 5);

        // Assert
        var failed = Assert.IsType<OutgoingHtlcFailed>(Assert.Single(_context.SwitchEvents));
        Assert.Equal(id, failed.HtlcId);
    }

    [Fact]
    public async Task Given_BeginReplayed_When_SameCommitment_Then_NoDuplicateRowsWatchesOrBroadcasts()
    {
        // Arrange
        Pair.Add(Pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        Pair.Settle(Pair.Alice);
        _context.UseSnapshot();
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
        var rows = _context.Store.Outputs.Count;
        var watches = _context.Tracked.Count;

        // Act
        var replay = await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);

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
        var commitment = _context.CloseWith(Pair.Alice.State.RemoteCommit, ChannelCloseKind.RemoteCommitment);
        await _context.BeginAsync(commitment, RemoteResolutionTestContext.CloseHeight);
        var sweep = Assert.Single(_context.Published);
        await _context.MineAsync(sweep, 501);
        var saves = _context.Store.Saves;
        var row = _context.ToRemoteRow();

        // Act
        var replay = await _context.MineAsync(sweep, 501);

        // Assert
        Assert.Empty(replay.Outputs);
        Assert.Equal(row, _context.ToRemoteRow());
        Assert.True(RemoteOutputData.Decode(row.DescriptorData).Spend!.ByUs);
        Assert.Equal(saves + 1, _context.Store.Saves);
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
        Assert.False(resolver.CanResolve(ChannelCloseKind.LocalCommitment));
        Assert.False(resolver.CanResolve(ChannelCloseKind.RevokedCommitment));
        Assert.False(resolver.CanResolve(ChannelCloseKind.Mutual));
    }

    private const uint SweepFeePolicyRbfSequence = Domain.Onchain.Fees.SweepFeePolicy.RbfSequence;

    public void Dispose() => _context.Dispose();
}