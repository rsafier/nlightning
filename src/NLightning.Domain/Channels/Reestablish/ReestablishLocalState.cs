namespace NLightning.Domain.Channels.Reestablish;

using Commitments;
using Enums;

/// <summary>
/// What <see cref="ReestablishPlanner"/> needs to know about our side of a channel (BOLT2 plan §3.11).
/// </summary>
/// <param name="LocalCommitmentNumber">L: the number of our current commitment (the last <c>commitment_signed</c> we
/// received was for it; we revoked L - 1).</param>
/// <param name="RemoteCommitmentNumber">R: the number of the peer's current (last revoked-up-to) commitment.</param>
/// <param name="HasRemoteNextCommit">We signed the peer's commitment R + 1 and wait for its <c>revoke_and_ack</c>.</param>
/// <param name="HasSentCommitDiff">The wire bytes of that <c>commitment_signed</c> and its updates are stored (D4).</param>
/// <param name="LastSent">Which of <c>commitment_signed</c>/<c>revoke_and_ack</c> we sent last.</param>
/// <param name="HasUnsignedLocalUpdates">We persisted updates that no <c>commitment_signed</c> covers yet (states 10
/// and 35, or our unsigned fee update): the peer dropped them on disconnect, so they are sent again.</param>
public sealed record ReestablishLocalState(
    ulong LocalCommitmentNumber,
    ulong RemoteCommitmentNumber,
    bool HasRemoteNextCommit,
    bool HasSentCommitDiff,
    LastSentCommitmentMessage LastSent,
    bool HasUnsignedLocalUpdates)
{
    /// <summary>
    /// The state of a channel with a commitment snapshot.
    /// </summary>
    /// <param name="commitments">The snapshot (after <see cref="ChannelCommitments.RevertUncommitted"/>).</param>
    /// <param name="hasSentCommitDiff">Whether the channel has a stored sent diff.</param>
    /// <param name="lastSent">The persisted <c>LastSentOrder</c>.</param>
    public static ReestablishLocalState From(ChannelCommitments commitments, bool hasSentCommitDiff,
                                             LastSentCommitmentMessage lastSent)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        return new ReestablishLocalState(commitments.LocalCommit.Number, commitments.RemoteCommit.Number,
                                         commitments.RemoteNextCommit is not null, hasSentCommitDiff, lastSent,
                                         HasUnsignedUpdates(commitments));
    }

    /// <summary>Our adds (10), our removals (35) and our fee update not covered by a <c>commitment_signed</c>.</summary>
    private static bool HasUnsignedUpdates(ChannelCommitments commitments) =>
        commitments.Htlcs.Values.Any(h => h.State is HtlcState.SentAddHtlc or HtlcState.SentRemoveHtlc)
     || commitments.FeeUpdates.Any(f => f.State == HtlcState.SentAddHtlc);
}