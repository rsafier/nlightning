namespace NLightning.Domain.Onchain.Models;

using Bitcoin.Transactions.Enums;
using Channels.Commitments;
using Channels.ValueObjects;

/// <summary>
/// One entry of the revocation log (BOLT 5 plan O1-T1, D2): a peer commitment the peer revoked, kept as its
/// <see cref="CommitmentSpec"/> (balances, feerate, HTLCs) so that, if the peer ever broadcasts it, every HTLC output
/// script can be rebuilt and penalized. The keys come from the basepoints and the per-commitment secret in the peer
/// shachain; nothing signed is stored.
/// </summary>
/// <remarks>
/// Written in the same save as the <c>revoke_and_ack</c> that revoked it, and only when the commitment had at least one
/// HTLC: without HTLCs its <c>to_local</c> penalty needs only the keys and the secret.
/// </remarks>
public sealed record RevokedCommitmentModel
{
    public ChannelId ChannelId { get; }

    /// <summary>The peer's commitment number.</summary>
    public ulong Number { get; }

    /// <summary>Its content (holder <see cref="CommitmentSide.Remote"/>).</summary>
    public CommitmentSpec Spec { get; }

    public RevokedCommitmentModel(ChannelId channelId, ulong number, CommitmentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Holder != CommitmentSide.Remote)
            throw new ArgumentException("A revoked commitment is a commitment of the peer", nameof(spec));

        ChannelId = channelId;
        Number = number;
        Spec = spec;
    }

    /// <summary>The log entry for <paramref name="revoked"/> (see <see cref="ChannelTransition.RevokedRemoteCommit"/>).
    /// </summary>
    public static RevokedCommitmentModel From(ChannelId channelId, RemoteCommit revoked)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        return new RevokedCommitmentModel(channelId, revoked.Number, revoked.Spec);
    }
}