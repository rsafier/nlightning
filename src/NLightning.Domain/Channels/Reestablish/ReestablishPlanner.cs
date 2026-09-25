namespace NLightning.Domain.Channels.Reestablish;

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
/// </remarks>
public static class ReestablishPlanner
{
    /// <summary>The length of <c>your_last_per_commitment_secret</c>.</summary>
    public const int SecretLength = 32;

    /// <summary>
    /// The numbers of our <c>channel_reestablish</c> (B2-RE-08..11): <c>next_commitment_number</c> = L + 1,
    /// <c>next_revocation_number</c> = R, the peer's secret R - 1 (none, i.e. zeroes, when R = 0) and our point L.
    /// </summary>
    public static OwnReestablish CreateOwn(ReestablishLocalState local)
    {
        ArgumentNullException.ThrowIfNull(local);
        var l = local.LocalCommitmentNumber;
        var r = local.RemoteCommitmentNumber;
        return new OwnReestablish(checked(l + 1), r, r == 0 ? null : r - 1, l);
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

        // A v1 channel never has an interactive funding transaction to finish: tell the peer to forget it
        if (peer.HasNextFunding)
            steps.Add(ReestablishStep.TxAbort);

        if (x == 0)
            return ReestablishPlan.Failed("B2-RE-14", "next_commitment_number is 0", steps, mustBroadcast: true);

        // Data loss first: the peer expects a revocation we never made and proves it with our own secret
        if (y > l)
        {
            if (SecretMatches(y, peer.YourLastPerCommitmentSecret, isOurSecret))
                return ReestablishPlan.LostData(
                    $"The peer expects revocation {y} but our current commitment is {l}, and it knows our secret {y - 1}");

            return ReestablishPlan.Failed("B2-RE-21",
                                          $"next_revocation_number {y} is ahead of our commitment {l} without proof",
                                          steps);
        }

        if (!SecretMatches(y, peer.YourLastPerCommitmentSecret, isOurSecret))
            return ReestablishPlan.Failed("B2-RE-24",
                                          $"your_last_per_commitment_secret is not our secret {(y == 0 ? "(zeroes)" : y - 1)}",
                                          steps);

        bool resendRevokeAndAck;
        if (y == l)
            resendRevokeAndAck = false;
        else if (l > 0 && y == l - 1)
            resendRevokeAndAck = true;
        else
            return ReestablishPlan.Failed("B2-RE-21",
                                          $"next_revocation_number {y} does not fit our commitment {l}", steps);

        bool resendCommitDiff;
        if (x == checked(r + 1))
        {
            // The peer is missing the commitment_signed we sent for R + 1, if we sent one
            resendCommitDiff = local.HasRemoteNextCommit;
            if (resendCommitDiff && !local.HasSentCommitDiff)
                return ReestablishPlan.Failed("B2-RE-18",
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
            return ReestablishPlan.Failed("B2-RE-19", $"next_commitment_number {x}, expected {expected}", steps);
        }

        if (x == 1 && l == 0)
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

        return ReestablishPlan.Resume(steps);
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
public sealed record OwnReestablish(
    ulong NextCommitmentNumber,
    ulong NextRevocationNumber,
    ulong? LastReceivedSecretNumber,
    ulong CurrentPointNumber);