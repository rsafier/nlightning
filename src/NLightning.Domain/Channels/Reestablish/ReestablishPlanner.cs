namespace NLightning.Domain.Channels.Reestablish;

using Bitcoin.ValueObjects;
using Enums;
using Protocol.Models;

/// <summary>
/// The pure BOLT 2 "Message Retransmission" rules (plan §3.11, N7-T1): the numbers we announce in our
/// <c>channel_reestablish</c> and what the peer's <c>channel_reestablish</c> means for us: retransmit, resume, fail, or
/// data loss.
/// </summary>
/// <remarks>
/// <para>
/// Notation: L = our current commitment number, R = the peer's current commitment number (the last one it revoked up
/// to), X/Y/S = the peer's <c>next_commitment_number</c>, <c>next_revocation_number</c> and
/// <c>your_last_per_commitment_secret</c>. A <c>revoke_and_ack</c> is numbered by the commitment it revokes.
/// </para>
/// <para>
/// Checks, in order: X = 0 fails (the spec also says to broadcast, B2-RE-14); Y &gt; L is data loss when S is our secret
/// Y - 1 (B2-RE-23), else a failure; S must be our secret Y - 1 (zeroes when Y = 0; B2-RE-24); Y = L - 1 resends our
/// last <c>revoke_and_ack</c> and Y = L needs none (else B2-RE-21); with a signed commitment awaiting its
/// <c>revoke_and_ack</c>, X = R + 1 resends it verbatim and X = R + 2 means the peer has it, without one X must be
/// R + 1 (else B2-RE-19); X = 1 and L = 0 resends <c>channel_ready</c> (B2-RE-15). Retransmitted
/// <c>revoke_and_ack</c> and <c>commitment_signed</c> keep their original order (<see cref="LastSentCommitmentMessage"/>,
/// B2-RE-20), and our unsigned updates follow.
/// </para>
/// <para>
/// Deviation from plan §3.11 step 3: the plan checks S against our secret L - 1, but a peer that missed our last
/// <c>revoke_and_ack</c> (Y = L - 1) legitimately holds only secret L - 2. The spec's rule is "the last secret it
/// received", i.e. secret Y - 1, which is what is checked here.
/// </para>
/// <para>
/// Interactive funding and splices (splicing plan SP-RE-01..06, SP2-A-T1): before the number checks, the peer's
/// <c>next_funding</c> naming our latest interactive transaction retransmits our <c>commitment_signed</c> for it (bit
/// 0) and our <c>tx_signatures</c> (when we received theirs, or theirs <c>commitment_signed</c> and we sign first); a
/// different txid while ours is set too fails the channel (SP-RE-03), any other one is answered with <c>tx_abort</c>.
/// <c>bolt02/splicing-test.md</c> still asks for the <c>commitment_signed</c> with X = R instead of bit 0 (the rule
/// before <c>retransmit_flags</c>); that X is accepted as R + 1 with bit 0 for our unsigned latest transaction.
/// <c>channel_ready</c> is not resent when a splice TLV is in either message (SP-RE-05). The peer's
/// <c>my_current_funding_locked</c> naming a pending splice whose <c>splice_locked</c> we lack becomes
/// <see cref="ReestablishPlan.Resume.PeerSpliceLocked"/>, and its bit 0 our <c>announcement_signatures</c> last
/// (SP-RE-04).
/// </para>
/// </remarks>
public static class ReestablishPlanner
{
    /// <summary>The length of <c>your_last_per_commitment_secret</c>.</summary>
    public const int SecretLength = 32;

    /// <summary>
    /// Our <c>channel_reestablish</c> (B2-RE-08..11): <c>next_commitment_number</c> = L + 1,
    /// <c>next_revocation_number</c> = R, the peer's secret R - 1 (none, i.e. zeroes, when R = 0) and our point L, with
    /// <c>next_funding</c> (SP-RE-01, <see cref="GetOwnNextFunding"/>) and <c>my_current_funding_locked</c> (SP-RE-02,
    /// <see cref="GetOwnFundingLocked"/>).
    /// </summary>
    /// <remarks>
    /// <c>next_commitment_number</c> stays L + 1 while the peer's <c>commitment_signed</c> for an interactive
    /// transaction is missing: BOLT 2 asks for it with the <c>commitment_signed</c> bit of <c>next_funding</c>
    /// (<c>bolt02/splicing-test.md</c> still shows the older convention, <c>next_commitment_number</c> = L, which
    /// <see cref="Plan"/> accepts from a peer).
    /// </remarks>
    public static OwnReestablish CreateOwn(ReestablishLocalState local)
    {
        ArgumentNullException.ThrowIfNull(local);
        var l = local.LocalCommitmentNumber;
        var r = local.RemoteCommitmentNumber;
        return new OwnReestablish(checked(l + 1), r, r == 0 ? null : r - 1, l, GetOwnNextFunding(local),
                                  GetOwnFundingLocked(local));
    }

    /// <summary>
    /// Our <c>next_funding</c> (SP-RE-01): the latest interactive transaction when we sent <c>commitment_signed</c> for
    /// it and did not receive <c>tx_signatures</c>, with the <c>commitment_signed</c> bit set when we did not receive
    /// the peer's; null otherwise.
    /// </summary>
    public static ReestablishFundingField? GetOwnNextFunding(ReestablishLocalState local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (local.LatestInteractiveTx is not { CommitmentSignedSent: true, TxSignaturesReceived: false } latest)
            return null;

        return new ReestablishFundingField(latest.TxId,
                                           latest.CommitmentSignedReceived
                                               ? (byte)0
                                               : ReestablishFundingField.CommitmentSignedFlag);
    }

    /// <summary>
    /// Our <c>my_current_funding_locked</c> (SP-RE-02), with <c>option_splice</c> only: the last splice we sent
    /// <c>splice_locked</c> for (a splice that reached its depth while disconnected is marked sent by the depth watcher),
    /// else the funding when we sent <c>channel_ready</c>, else null; bit 0 set on a public channel when we hold no
    /// <c>announcement_signatures</c> of the peer for that txid.
    /// </summary>
    public static ReestablishFundingField? GetOwnFundingLocked(ReestablishLocalState local)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (local.Splice is not { } splice)
            return null;

        TxId txId;
        if (splice.LastSpliceLockedSent is { } lastSpliceLocked)
            txId = lastSpliceLocked;
        else if (splice.ChannelReadySent)
            txId = splice.CurrentFundingTxId;
        else
            return null;

        var askAnnouncement = splice.AnnounceChannel && !splice.AnnouncementSignaturesReceivedFor.Contains(txId);
        return new ReestablishFundingField(txId,
                                           askAnnouncement
                                               ? ReestablishFundingField.AnnouncementSignaturesFlag
                                               : (byte)0);
    }

    /// <summary>
    /// Judges the peer's <c>channel_reestablish</c>.
    /// </summary>
    /// <param name="local">Our side (after <c>RevertUncommitted</c>).</param>
    /// <param name="peer">The peer's message.</param>
    /// <param name="isOurSecret">True when the 32 bytes are our per-commitment secret for the given commitment number
    /// (checked against our per-commitment point, so no secret is revealed to check it).</param>
    public static ReestablishPlan Plan(ReestablishLocalState local, PeerReestablish peer,
                                       Func<ulong, ReadOnlyMemory<byte>, bool> isOurSecret)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(isOurSecret);

        var l = local.LocalCommitmentNumber;
        var r = local.RemoteCommitmentNumber;
        var x = peer.NextCommitmentNumber;
        var y = peer.NextRevocationNumber;
        var steps = new List<ReestablishStep>();

        // next_funding (SP-RE-03, B2-RE-25): the signing steps of our latest interactive transaction are finished, a
        // different txid while we set next_funding too fails the channel, anything else is forgotten with tx_abort (a
        // v1 channel never has one)
        var latest = local.LatestInteractiveTx;
        var peerNextFunding = peer.NextFunding;
        var namesLatest = peerNextFunding is not null && latest is not null && latest.TxId == peerNextFunding.TxId;
        if (namesLatest)
        {
            // bolt02/splicing-test.md still asks for our commitment_signed with next_commitment_number equal to the
            // peer's current number instead of the flag: accepted as the flag
            var legacyRequest = x == r && latest is { TxSignaturesReceived: false, CommitmentSignedSent: true }
                                       && !local.HasRemoteNextCommit;
            if (legacyRequest)
                x = checked(r + 1);

            if (latest!.TxSignaturesReceived)
            {
                steps.Add(ReestablishStep.NextFundingTxSignatures);
            }
            else
            {
                if ((peerNextFunding!.IsBit0Set || legacyRequest) && latest.CommitmentSignedSent)
                    steps.Add(ReestablishStep.NextFundingCommitmentSigned);
                if (latest is { CommitmentSignedReceived: true, SendsTxSignaturesFirst: true } || latest.TxSignaturesSent)
                    steps.Add(ReestablishStep.NextFundingTxSignatures);
            }
        }
        else if (peerNextFunding is not null && GetOwnNextFunding(local) is { } ownNextFunding)
        {
            return new ReestablishPlan.Fail("SP-RE-03",
                                            $"next_funding {peerNextFunding.TxId} differs from ours {ownNextFunding.TxId}",
                                            []);
        }
        else if (peerNextFunding is not null || peer.HasNextFunding)
        {
            steps.Add(ReestablishStep.TxAbort);
        }

        if (x == 0)
            return new ReestablishPlan.Fail("B2-RE-14", "next_commitment_number is 0", steps, MustBroadcast: true);

        // Data loss first: the peer expects a revocation we never made and proves it with our own secret
        if (y > l)
        {
            if (SecretMatches(y, peer.YourLastPerCommitmentSecret, isOurSecret))
                return new ReestablishPlan.DataLoss(
                    $"The peer expects revocation {y} but our current commitment is {l}, and it knows our secret {y - 1}");

            return new ReestablishPlan.Fail("B2-RE-21",
                                            $"next_revocation_number {y} is ahead of our commitment {l} without proof",
                                            steps);
        }

        if (!SecretMatches(y, peer.YourLastPerCommitmentSecret, isOurSecret))
            return new ReestablishPlan.Fail("B2-RE-24",
                                            $"your_last_per_commitment_secret is not our secret {(y == 0 ? "(zeroes)" : y - 1)}",
                                            steps);

        bool resendRevokeAndAck;
        if (y == l)
            resendRevokeAndAck = false;
        else if (l > 0 && y == l - 1)
            resendRevokeAndAck = true;
        else
            return new ReestablishPlan.Fail("B2-RE-21",
                                            $"next_revocation_number {y} does not fit our commitment {l}", steps);

        bool resendCommitDiff;
        if (x == checked(r + 1))
        {
            // The peer is missing the commitment_signed we sent for R + 1, if we sent one
            resendCommitDiff = local.HasRemoteNextCommit;
            if (resendCommitDiff && !local.HasSentCommitDiff)
                return new ReestablishPlan.Fail("B2-RE-18",
                                                $"commitment_signed {x} must be retransmitted but it is not stored",
                                                steps);
        }
        else if (local.HasRemoteNextCommit && x == checked(r + 2))
        {
            // The peer has it and will retransmit its revoke_and_ack
            resendCommitDiff = false;
        }
        else
        {
            var expected = local.HasRemoteNextCommit ? $"{r + 1} or {r + 2}" : $"{r + 1}";
            return new ReestablishPlan.Fail("B2-RE-19", $"next_commitment_number {x}, expected {expected}", steps);
        }

        // SP-RE-05: not when a message of the exchange carries a splice's funding TLV
        if (x == 1 && l == 0 && !CarriesSpliceFunding(local, peer, namesLatest))
            steps.Add(ReestablishStep.ChannelReady);

        if (resendRevokeAndAck && resendCommitDiff)
        {
            // Same relative order as first sent: the one sent last goes last
            if (local.LastSent == LastSentCommitmentMessage.RevokeAndAck)
                steps.AddRange([ReestablishStep.CommitDiff, ReestablishStep.RevokeAndAck]);
            else
                steps.AddRange([ReestablishStep.RevokeAndAck, ReestablishStep.CommitDiff]);
        }
        else if (resendRevokeAndAck)
        {
            steps.Add(ReestablishStep.RevokeAndAck);
        }
        else if (resendCommitDiff)
        {
            steps.Add(ReestablishStep.CommitDiff);
        }

        if (local.HasUnsignedLocalUpdates)
            steps.Add(ReestablishStep.UnsignedUpdates);

        // SP-RE-04 (option_splice only): my_current_funding_locked naming a pending splice whose splice_locked we lack
        // is processed as that splice_locked; its bit 0 asks for our announcement_signatures of that funding
        TxId? peerSpliceLocked = null;
        if (local.Splice is { } splice && peer.MyCurrentFundingLocked is { } fundingLocked)
        {
            if (splice.PendingSplices.FirstOrDefault(p => p.TxId == fundingLocked.TxId) is
                { SpliceLockedReceived: false })
                peerSpliceLocked = fundingLocked.TxId;

            if (fundingLocked.IsBit0Set && splice.AnnounceChannel
             && (splice.AnnouncementSignaturesReadyFor.Contains(fundingLocked.TxId)
              || peerSpliceLocked == fundingLocked.TxId))
                steps.Add(ReestablishStep.AnnouncementSignatures);
        }

        return new ReestablishPlan.Resume(steps, peerSpliceLocked);
    }

    /// <summary>
    /// SP-RE-05: whether our <c>channel_reestablish</c> or the peer's carries <c>next_funding</c> or
    /// <c>my_current_funding_locked</c> for a splice transaction. Without <c>option_splice</c> nothing is a splice. With
    /// it, the peer's <c>next_funding</c> counts unless it names our latest interactive transaction and that is a
    /// dual-funded open (an unknown txid is taken as a splice); its <c>my_current_funding_locked</c> counts when it names
    /// one of our splices.
    /// </summary>
    private static bool CarriesSpliceFunding(ReestablishLocalState local, PeerReestablish peer, bool peerNamesLatest)
    {
        var latest = local.LatestInteractiveTx;
        if (latest is { IsSplice: true } && GetOwnNextFunding(local) is not null)
            return true;

        if (local.Splice is not { } splice)
            return false;

        if (GetOwnFundingLocked(local) is { } ownLocked && splice.IsSplice(ownLocked.TxId))
            return true;
        if (peer.MyCurrentFundingLocked is { } peerLocked && splice.IsSplice(peerLocked.TxId))
            return true;

        return (peer.NextFunding is not null || peer.HasNextFunding)
            && !(peerNamesLatest && latest is { IsSplice: false });
    }

    /// <summary>
    /// S must be all zeroes when Y is 0, else our secret Y - 1.
    /// </summary>
    private static bool SecretMatches(ulong nextRevocationNumber, ReadOnlyMemory<byte> secret,
                                      Func<ulong, ReadOnlyMemory<byte>, bool> isOurSecret)
    {
        if (secret.Length != SecretLength)
            return false;

        if (nextRevocationNumber == 0)
            return !secret.Span.ContainsAnyExcept((byte)0);

        var number = nextRevocationNumber - 1;
        return number <= PerCommitmentIndex.MaxCommitmentNumber && isOurSecret(number, secret);
    }
}

/// <summary>The numbers of our <c>channel_reestablish</c>.</summary>
/// <param name="NextCommitmentNumber">L + 1.</param>
/// <param name="NextRevocationNumber">R.</param>
/// <param name="LastReceivedSecretNumber">R - 1: the peer's commitment whose secret we send back, or null for zeroes.
/// </param>
/// <param name="CurrentPointNumber">L: the commitment whose per-commitment point we send.</param>
/// <param name="NextFunding">Our <c>next_funding</c> TLV (SP-RE-01), or null (splicing plan SP2-0).</param>
/// <param name="MyCurrentFundingLocked">Our <c>my_current_funding_locked</c> TLV (SP-RE-02), or null (splicing plan
/// SP2-0).</param>
public sealed record OwnReestablish(
    ulong NextCommitmentNumber,
    ulong NextRevocationNumber,
    ulong? LastReceivedSecretNumber,
    ulong CurrentPointNumber,
    ReestablishFundingField? NextFunding = null,
    ReestablishFundingField? MyCurrentFundingLocked = null);