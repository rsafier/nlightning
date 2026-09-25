namespace NLightning.Domain.Channels.Reestablish;

/// <summary>
/// The fields of the peer's <c>channel_reestablish</c> the planner judges.
/// </summary>
/// <param name="NextCommitmentNumber">X: the number of the next <c>commitment_signed</c> the peer expects from us.
/// </param>
/// <param name="NextRevocationNumber">Y: the number of the next <c>revoke_and_ack</c> the peer expects from us (the
/// number of the commitment of ours it expects to see revoked).</param>
/// <param name="YourLastPerCommitmentSecret">S: the last per-commitment secret of ours the peer received (32 bytes;
/// all zeroes when <paramref name="NextRevocationNumber"/> is 0).</param>
/// <param name="HasNextFunding">The message carries the <c>next_funding</c> TLV (interactive funding only).</param>
public sealed record PeerReestablish(
    ulong NextCommitmentNumber,
    ulong NextRevocationNumber,
    ReadOnlyMemory<byte> YourLastPerCommitmentSecret,
    bool HasNextFunding = false);