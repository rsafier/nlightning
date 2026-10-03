namespace NLightning.Domain.Gossip.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Models;
using Money;

/// <summary>
/// Finds the funding output a <c>channel_announcement</c>'s short channel id names (BOLT 7 plan §3.4, D3): the block
/// at the SCID's height, its transaction at the SCID's index, and that transaction's output at the SCID's output
/// index, which must be confirmed and unspent. Needs no txindex.
/// </summary>
/// <remarks>
/// Lookups are bounded in concurrency and rate to protect bitcoind, so a call may wait. Transient failures (bitcoind
/// down, a reorg during the lookup) are results (<see cref="FundingOutputLookupResult.IsTransient"/>), never
/// exceptions; only cancellation throws. The depth rule (6 confirmations) is the caller's, from
/// <see cref="FundingOutputLookupResult.Confirmations"/>.
/// </remarks>
public interface IFundingOutputLookup
{
    /// <summary>The output <paramref name="shortChannelId"/> names, without checking its script.</summary>
    Task<FundingOutputLookupResult> LookupAsync(ShortChannelId shortChannelId,
                                                CancellationToken cancellationToken = default);

    /// <summary>
    /// The output <paramref name="shortChannelId"/> names, checked to be the P2WSH of
    /// <c>2 &lt;key1&gt; &lt;key2&gt; 2 OP_CHECKMULTISIG</c> with the two bitcoin keys in BOLT 3 (lexicographic) order,
    /// with a non-zero amount equal to <paramref name="expectedAmount"/> when one is given.
    /// </summary>
    Task<FundingOutputLookupResult> VerifyAsync(ShortChannelId shortChannelId, CompactPubKey bitcoinKey1,
                                                CompactPubKey bitcoinKey2, LightningMoney? expectedAmount = null,
                                                CancellationToken cancellationToken = default);

    /// <summary>Forgets every cached block at <paramref name="height"/> or above (a reorg).</summary>
    void InvalidateFrom(uint height);
}