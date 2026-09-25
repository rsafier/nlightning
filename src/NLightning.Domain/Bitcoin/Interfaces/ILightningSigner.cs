namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// Interface for transaction signing services that can be implemented either locally 
/// or delegated to external services like VLS (Validating Lightning Signer)
/// </summary>
public interface ILightningSigner
{
    /// <summary>
    /// Generate a new channel key set and return the channel key index
    /// </summary>
    uint CreateNewChannel(out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint);

    /// <summary>
    /// Generate or retrieve channel basepoints for a channel
    /// </summary>
    ChannelBasepoints GetChannelBasepoints(uint channelKeyIndex);

    /// <summary>
    /// Generate or retrieve channel basepoints for a channel
    /// </summary>
    ChannelBasepoints GetChannelBasepoints(ChannelId channelId);

    /// <summary>
    /// Get the node's public key
    /// </summary>
    CompactPubKey GetNodePublicKey();

    /// <summary>
    /// Generate the per-commitment point of one of our commitment transactions.
    /// </summary>
    /// <param name="channelKeyIndex">The channel key index.</param>
    /// <param name="commitmentNumber">
    /// The commitment number (0 for the first commitment), <b>not</b> a BOLT 3 index: the signer derives the secret at
    /// index <c>2^48 - 1 - commitmentNumber</c> (<see cref="Protocol.Models.PerCommitmentIndex"/>).
    /// </param>
    CompactPubKey GetPerCommitmentPoint(uint channelKeyIndex, ulong commitmentNumber);

    /// <summary>
    /// Generate the per-commitment point of one of our commitment transactions.
    /// </summary>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="commitmentNumber">
    /// The commitment number (0 for the first commitment), <b>not</b> a BOLT 3 index: the signer derives the secret at
    /// index <c>2^48 - 1 - commitmentNumber</c> (<see cref="Protocol.Models.PerCommitmentIndex"/>).
    /// </param>
    CompactPubKey GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber);

    /// <summary>
    /// Store channel information needed for signing
    /// </summary>
    void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo);

    /// <summary>
    /// Release (reveal) the per-commitment secret of one of our commitment transactions, for revocation.
    /// </summary>
    /// <param name="channelKeyIndex">The channel key index.</param>
    /// <param name="commitmentNumber">The commitment number (not a BOLT 3 index), see
    /// <see cref="GetPerCommitmentPoint(uint, ulong)"/>.</param>
    Secret ReleasePerCommitmentSecret(uint channelKeyIndex, ulong commitmentNumber);

    /// <summary>
    /// Release (reveal) the per-commitment secret of one of our commitment transactions, for revocation.
    /// </summary>
    /// <param name="channelId">The registered channel.</param>
    /// <param name="commitmentNumber">The commitment number (not a BOLT 3 index), see
    /// <see cref="GetPerCommitmentPoint(ChannelId, ulong)"/>.</param>
    Secret ReleasePerCommitmentSecret(ChannelId channelId, ulong commitmentNumber);

    /// <summary>
    /// Sign a general transaction using the wallet signing context
    /// </summary>
    bool SignWalletTransaction(SignedTransaction unsignedTransaction);

    /// <summary>
    /// Sign a funding transaction using the wallet signing context  and validating using the channel context
    /// </summary>
    bool SignFundingTransaction(ChannelId channelId, SignedTransaction unsignedTransaction);

    /// <summary>
    /// Sign a transaction using the channel's signing context
    /// </summary>
    CompactSignature SignChannelTransaction(ChannelId channelId, SignedTransaction unsignedTransaction);

    /// <summary>
    /// Verify a signature against a transaction
    /// </summary>
    void ValidateSignature(ChannelId channelId, CompactSignature signature, SignedTransaction unsignedTransaction);
}