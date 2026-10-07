namespace NLightning.Domain.Channels.Splicing;

using Bitcoin.Transactions.Outputs;
using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// One funding output of a channel (splicing plan §3.3): the channel's initial funding, or a splice (or splice RBF
/// attempt) of it. The HTLC set, fee updates and commitment numbers are shared by every active funding; only the
/// funding outpoint, the capacity and the main balances (through the deltas) differ.
/// </summary>
/// <remarks>
/// <para>Scalar-only on purpose, so value equality holds: the splice transaction itself (constructed, witnesses) lives
/// in the interactive-tx session row that produced it (<c>InteractiveTxSessions</c>, keyed by channel and txid), not
/// here.</para>
/// <para>Persisted by lane SP1-C (<c>ChannelFundings</c>, PK <c>ChannelId, FundingTxId</c>); the engine behaviour
/// (per-funding specs, conservation I6 per funding) is lane SP1-B's.</para>
/// </remarks>
/// <param name="FundingTxId">The funding (or splice) transaction id.</param>
/// <param name="OutputIndex">The funding output index in that transaction.</param>
/// <param name="CapacitySatoshis">The funding output amount.</param>
/// <param name="LocalFundingPubKey">Our funding key in the 2-of-2 script of this funding.</param>
/// <param name="RemoteFundingPubKey">The peer's funding key in the 2-of-2 script of this funding.</param>
/// <param name="LocalFundingKeyIndex">The derivation index of our funding key (splicing plan D5: 0 for the initial
/// funding, rotated per splice, deterministic so a static channel backup can restore it).</param>
/// <param name="LocalBalanceDeltaMsat">Our main balance on this funding minus our main balance on the current one (our
/// contribution, fees included, D16); 0 for the current funding.</param>
/// <param name="RemoteBalanceDeltaMsat">The same for the peer.</param>
/// <param name="Kind">How the funding was created.</param>
/// <param name="Status">Where the funding stands.</param>
/// <param name="FeeratePerKw">The feerate the splice transaction was negotiated at; null for an initial v1
/// funding.</param>
/// <param name="Locktime">The splice transaction's <c>nLockTime</c>; null for an initial v1 funding.</param>
/// <param name="RbfOf">For <see cref="ChannelFundingKind.SpliceRbf"/>: the splice attempt it replaces.</param>
/// <param name="ConfirmedHeight">The height the funding transaction confirmed at, once it did.</param>
/// <param name="ShortChannelId">The funding's real short channel id, once confirmed.</param>
/// <param name="SpliceLockedSent">We sent <c>splice_locked</c> for this funding (SP-LK-01).</param>
/// <param name="SpliceLockedReceived">The peer sent <c>splice_locked</c> for this funding.</param>
/// <param name="AnnouncementSignaturesReceived">The peer's <c>announcement_signatures</c> for this funding arrived
/// (SP-RE-02 <c>retransmit_flags</c>).</param>
/// <param name="FundingKeysUnknown">Recovery knows the outpoint but not both funding keys; key fields are
/// historical hints and must never be used to sign this funding.</param>
public sealed record ChannelFunding(
    TxId FundingTxId,
    ushort OutputIndex,
    ulong CapacitySatoshis,
    CompactPubKey LocalFundingPubKey,
    CompactPubKey RemoteFundingPubKey,
    uint LocalFundingKeyIndex,
    long LocalBalanceDeltaMsat,
    long RemoteBalanceDeltaMsat,
    ChannelFundingKind Kind,
    ChannelFundingStatus Status,
    uint? FeeratePerKw = null,
    uint? Locktime = null,
    TxId? RbfOf = null,
    uint? ConfirmedHeight = null,
    ShortChannelId? ShortChannelId = null,
    bool SpliceLockedSent = false,
    bool SpliceLockedReceived = false,
    bool AnnouncementSignaturesReceived = false,
    bool FundingKeysUnknown = false)
{
    /// <summary>The capacity in millisatoshis.</summary>
    public ulong CapacityMsat => checked(CapacitySatoshis * 1_000);

    /// <summary>Whether commitments are signed for this funding (<see cref="ChannelFundingStatus.Current"/> or
    /// <see cref="ChannelFundingStatus.Pending"/>, SP-OP-01).</summary>
    public bool IsActive => Status is ChannelFundingStatus.Current or ChannelFundingStatus.Pending;

    /// <summary>
    /// The channel's initial funding as today's single-funding code knows it: the current funding, no deltas, funding
    /// key index 0.
    /// </summary>
    /// <param name="fundingOutput">The channel's funding output (<c>ChannelModel.FundingOutput</c>) with its
    /// outpoint known.</param>
    /// <returns>The funding, or null when the outpoint is not known yet (before <c>funding_created</c>).</returns>
    public static ChannelFunding? FromFundingOutput(FundingOutputInfo fundingOutput)
    {
        ArgumentNullException.ThrowIfNull(fundingOutput);
        if (fundingOutput.TransactionId is not { } txId || fundingOutput.Index is not { } index)
            return null;

        return new ChannelFunding(txId, index, (ulong)fundingOutput.Amount.Satoshi, fundingOutput.LocalFundingPubKey,
                                  fundingOutput.RemoteFundingPubKey, 0, 0, 0, ChannelFundingKind.Initial,
                                  ChannelFundingStatus.Current);
    }
}