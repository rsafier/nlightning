namespace NLightning.Domain.Channels.Models;

using Crypto.Constants;
using Crypto.ValueObjects;

public class ChannelKeySetModel
{
    public uint KeyIndex { get; }
    public CompactPubKey FundingCompactPubKey { get; }
    public CompactPubKey RevocationCompactBasepoint { get; }
    public CompactPubKey PaymentCompactBasepoint { get; }
    public CompactPubKey DelayedPaymentCompactBasepoint { get; }

    public CompactPubKey HtlcCompactBasepoint { get; }
    public CompactPubKey CurrentPerCommitmentCompactPoint { get; private set; }
    public ulong CurrentPerCommitmentIndex { get; private set; }

    /// <summary>
    /// Legacy single-secret store, kept only so the existing <c>ChannelKeySets.LastRevealedPerCommitmentSecret</c>
    /// column still round-trips. Nothing writes it any more: the peer's revealed secrets live in the shachain
    /// (<c>ISecretStorageService</c> + <c>IRemoteShachainDbRepository</c>, BOLT2 plan N3-T4 / G19), which is the only
    /// source for <c>your_last_per_commitment_secret</c> and penalty data.
    /// </summary>
    [Obsolete("Use the shachain (ISecretStorageService + IRemoteShachainDbRepository) for the peer's secrets")]
    public byte[]? LastRevealedPerCommitmentSecret { get; private set; }

    public ChannelKeySetModel(uint keyIndex, CompactPubKey fundingCompactPubKey,
                              CompactPubKey revocationCompactBasepoint, CompactPubKey paymentCompactBasepoint,
                              CompactPubKey delayedPaymentCompactBasepoint, CompactPubKey htlcCompactBasepoint,
                              CompactPubKey currentPerCommitmentCompactPoint,
                              ulong currentPerCommitmentIndex = CryptoConstants.FirstPerCommitmentIndex,
                              byte[]? lastRevealedPerCommitmentSecret = null)
    {
        KeyIndex = keyIndex;
        FundingCompactPubKey = fundingCompactPubKey;
        RevocationCompactBasepoint = revocationCompactBasepoint;
        PaymentCompactBasepoint = paymentCompactBasepoint;
        DelayedPaymentCompactBasepoint = delayedPaymentCompactBasepoint;
        HtlcCompactBasepoint = htlcCompactBasepoint;
        CurrentPerCommitmentCompactPoint = currentPerCommitmentCompactPoint;
        CurrentPerCommitmentIndex = currentPerCommitmentIndex;
#pragma warning disable CS0618 // legacy column round-trip only
        LastRevealedPerCommitmentSecret = lastRevealedPerCommitmentSecret;
#pragma warning restore CS0618
    }

    public void UpdatePerCommitmentPoint(CompactPubKey newPoint)
    {
        CurrentPerCommitmentCompactPoint = newPoint;
        CurrentPerCommitmentIndex--;
    }

    /// <summary>
    /// Create a ChannelKeySet for the remote party (we don't generate their keys)
    /// </summary>
    public static ChannelKeySetModel CreateForRemote(CompactPubKey fundingPubKey, CompactPubKey revocationBasepoint,
                                                     CompactPubKey paymentBasepoint,
                                                     CompactPubKey delayedPaymentBasepoint, CompactPubKey htlcBasepoint,
                                                     CompactPubKey firstPerCommitmentPoint)
    {
        return new ChannelKeySetModel(0, fundingPubKey, revocationBasepoint, paymentBasepoint, delayedPaymentBasepoint,
                                      htlcBasepoint, firstPerCommitmentPoint);
    }
}