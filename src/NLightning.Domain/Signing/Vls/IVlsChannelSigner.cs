namespace NLightning.Domain.Signing.Vls;

using Bitcoin.Transactions.Models;
using Bitcoin.ValueObjects;
using Channels.Commitments;
using Channels.Models;
using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// Policy-aware channel operations. Generic transaction signatures must never substitute for these operations.
/// Models contain final fee-adjusted outputs and untrimmed HTLCs relative to the broadcaster.
/// </summary>
public interface IVlsChannelSigner
{
    uint CreateNewChannel(CompactPubKey peer, out ChannelBasepoints basepoints,
                          out CompactPubKey firstPerCommitmentPoint);

    void EnsureChannelSetup(ChannelModel channel);

    CommitmentSignatures SignCounterpartyCommitment(ChannelModel channel, CommitmentTransactionModel commitment);

    void ValidateHolderCommitment(ChannelModel channel, CommitmentTransactionModel commitment,
                                  CompactSignature signature, IReadOnlyList<CompactSignature> htlcSignatures);

    /// <summary>Activate commitment zero only after the holder signature is durably stored by the node.</summary>
    void ActivateChannel(ChannelModel channel);

    /// <summary>Release a prior holder secret only after the next holder commitment is durably stored.</summary>
    Secret RevokeHolderCommitment(ChannelId channelId, ulong revokedCommitmentNumber);

    void ValidatePeerRevocation(ChannelId channelId, ulong revokedCommitmentNumber, Secret secret,
                                CompactPubKey? nextPerCommitmentPoint = null);

    CompactSignature SignMutualClose(ChannelModel channel, ulong holderSatoshis, ulong peerSatoshis,
                                    BitcoinScript? holderScript, BitcoinScript? peerScript);
}