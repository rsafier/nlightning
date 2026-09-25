namespace NLightning.Domain.Channels.Models;

using Enums;
using Protocol.Models;

/// <summary>
/// What a channel transition persists besides the <see cref="Commitments.ChannelCommitments"/> snapshot, in the same
/// save (decision D3). A null member means "unchanged".
/// </summary>
/// <remarks>
/// <see cref="SentCommitDiff"/> is cleared automatically once the snapshot has no
/// <see cref="Commitments.ChannelCommitments.RemoteNextCommit"/> (the peer revoked, decision D4).
/// </remarks>
public sealed record ChannelStateExtras
{
    /// <summary>
    /// The wire bytes of every update sent since the previous <c>commitment_signed</c> followed by the new
    /// <c>commitment_signed</c>, retransmitted verbatim on reestablish (decision D4). Set when we sign.
    /// </summary>
    public ReadOnlyMemory<byte>? SentCommitDiff { get; init; }

    /// <summary>Which of <c>commitment_signed</c>/<c>revoke_and_ack</c> this transition sent last.</summary>
    public LastSentCommitmentMessage? LastSent { get; init; }

    /// <summary>
    /// The peer's whole shachain after this transition (<c>ISecretStorageService.Export()</c>), stored in the same save
    /// as the <c>revoke_and_ack</c> that revealed the new secret (B2-RAA-R04).
    /// </summary>
    public IReadOnlyList<ShachainEntry>? RemoteShachain { get; init; }
}