namespace NLightning.Application.Channels.Taproot;

using Close.Simple;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Models;

/// <summary>
/// The MuSig2 nonces the simple taproot channel handlers exchange (bolt-simple-taproot.md §Channel Funding, §Channel
/// Operation, §Message Retransmission; NL-877 T3/T5): our verification nonces, re-derived by the signer from the counter
/// scheme (D-T4, never stored), and the checks of the peer's.
/// </summary>
/// <remarks>
/// Our verification nonce for local commitment <c>n</c> is what the peer signs our commitment <c>n</c> against:
/// <c>open_channel</c>/<c>accept_channel</c> carry commitment 0's, <c>channel_ready</c> commitment 1's, a
/// <c>revoke_and_ack</c> that revokes <c>n - 2</c> (we now hold <c>n - 1</c>) commitment <c>n</c>'s, and
/// <c>channel_reestablish</c> the one after our current commitment. The same number always gives the same nonce, so a
/// retransmission repeats it; the peer's signing nonce is fresh for every signature it makes.
/// </remarks>
public static class TaprootChannelNonces
{
    /// <summary>
    /// The channel's active fundings, the current one first: the engine's current funding and pending splices once the
    /// channel has a commitment state, else (before <c>channel_ready</c>) the channel's funding output.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel has no funding txid yet.</exception>
    public static IReadOnlyList<TxId> GetActiveFundingTxIds(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Commitments is { Params.Funding: { } current } commitments)
            return [current.FundingTxId, .. commitments.PendingFundings.Select(f => f.FundingTxId)];

        return channel.FundingOutput?.TransactionId is { } txId
                   ? [txId]
                   : throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding txid yet");
    }

    /// <summary>
    /// Our <c>next_local_nonces</c>: per active funding, our verification nonce for our local commitment
    /// <paramref name="localCommitmentNumber"/> on it.
    /// </summary>
    public static FundingNonces CreateLocalNonces(ILightningSigner signer, ChannelModel channel,
                                                  ulong localCommitmentNumber)
    {
        ArgumentNullException.ThrowIfNull(signer);
        return new FundingNonces(GetActiveFundingTxIds(channel)
                                    .Select(txId => (txId, signer.GetLocalVerificationNonce(
                                                               channel.ChannelId, txId, localCommitmentNumber))));
    }

    /// <summary>
    /// Our <c>next_local_nonces</c> of a dual-funded open still waiting for its funding (no commitment state yet): an
    /// entry for every fully signed attempt (<paramref name="signedAttempts"/>, any of them may confirm) and for the
    /// channel's funding output (an RBF attempt still being signed), as Eclair 0.14.3 sends them for its active
    /// commitments and its RBF signing session (NL-970). Derived through the key index (every attempt is a funding on
    /// our original key, bound to its txid), so a replaced attempt the signer no longer signs for still has its nonce.
    /// </summary>
    public static FundingNonces CreatePendingOpenNonces(ILightningSigner signer, ChannelModel channel,
                                                        IEnumerable<TxId> signedAttempts, ulong localCommitmentNumber)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(signedAttempts);
        var keyIndex = channel.LocalKeySet.KeyIndex;
        return new FundingNonces(signedAttempts.Concat(GetActiveFundingTxIds(channel))
                                               .Distinct()
                                               .Select(txId => (txId, signer.GetLocalVerificationNonce(
                                                                          keyIndex, txId, localCommitmentNumber))));
    }

    /// <summary>
    /// Our verification nonce for local commitment <paramref name="localCommitmentNumber"/> on the channel's current
    /// funding (the <c>next_local_nonce</c> of <c>channel_ready</c>).
    /// </summary>
    public static MusigPublicNonce GetCurrentFundingNonce(ILightningSigner signer, ChannelModel channel,
                                                          ulong localCommitmentNumber)
    {
        ArgumentNullException.ThrowIfNull(signer);
        return signer.GetLocalVerificationNonce(channel.ChannelId, GetActiveFundingTxIds(channel)[0],
                                                localCommitmentNumber);
    }

    /// <summary>
    /// The peer's nonce map as the engine takes it (<c>ChannelCommitments.ReceiveRemoteNonces</c>/<c>ReceiveRevoke</c>).
    /// </summary>
    public static IReadOnlyDictionary<TxId, MusigPublicNonce> ToDictionary(FundingNonces nonces)
    {
        ArgumentNullException.ThrowIfNull(nonces);
        return nonces.Entries.ToDictionary(e => e.FundingTxId, e => e.Nonce);
    }

    /// <summary>
    /// The peer's <c>next_local_nonces</c> of a <c>revoke_and_ack</c> or <c>channel_reestablish</c>: every entry must
    /// parse as two compressed secp256k1 points (bolt-simple-taproot.md §Message Retransmission: "MUST fail the channel
    /// if <c>next_local_nonces</c> is absent, or cannot be parsed"; NL-975). A nonce that is not one would be saved and
    /// then make every <c>commitment_signed</c> of ours fail in the signer, so the channel would hang until its HTLC
    /// deadlines instead of failing.
    /// </summary>
    /// <exception cref="ChannelFailedException">An entry does not parse (TAPROOT-NONCE-R02).</exception>
    public static void ThrowIfUnparsable(ChannelModel channel, FundingNonces nonces, string messageName)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(nonces);
        foreach (var (txId, nonce) in nonces.Entries)
            if (!TaprootCloseNonces.IsValidPublicNonce(nonce))
                throw new ChannelFailedException(channel.ChannelId,
                                                 $"[TAPROOT-NONCE-R02] {messageName} next_local_nonces entry for "
                                               + $"funding {txId} is not two compressed points",
                                                 $"{messageName} next_local_nonces does not parse")
                {
                    RequirementId = "TAPROOT-NONCE-R02"
                };
    }

    /// <summary>
    /// Whether <paramref name="nonce"/> parses as two compressed secp256k1 points (the spec's "MUST fail the channel if
    /// ... cannot be parsed as two compressed secp256k1 points"): BIP 327 <c>NonceAgg</c> of it alone decodes both
    /// halves and blames an undecodable one.
    /// </summary>
    public static bool IsValidPublicNonce(IMusig2Service musig2, MusigPublicNonce nonce)
    {
        ArgumentNullException.ThrowIfNull(musig2);
        try
        {
            _ = musig2.AggregateNonces([nonce]);
            return true;
        }
        catch (MusigException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}