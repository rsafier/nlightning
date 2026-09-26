namespace NLightning.Domain.Protocol.Onion.Interfaces;

using Channels.ValueObjects;

/// <summary>
/// The replay set of incoming payment onions, keyed by packet HMAC and kept until the HTLC's <c>cltv_expiry</c>
/// (NL-078). The node uses the persistent implementation (table <c>OnionReplayEntries</c>); an in-memory one serves
/// compositions without a database.
/// </summary>
/// <remarks>
/// <para>
/// BOLT 4: "if the onion is for a payment: if <c>hmac</c> has previously been received: if the preimage is known MAY
/// immediately redeem the HTLC using the preimage, otherwise MUST abort processing the packet and fail." Record an
/// HMAC only after the peel verified it: recording unverified HMACs would let a peer fill the set with random values at
/// no crypto cost (and, in a bounded store, evict genuine entries and then replay an onion it forwarded earlier).
/// </para>
/// <para>
/// Each entry remembers the incoming HTLC that first carried the onion. Adding the same HMAC again for that same HTLC
/// is not a replay: the switch re-processes a locked-in HTLC after a restart or on every link-up, and a persistent store
/// would otherwise report its own HTLC as a replay whenever the process stopped between recording the HMAC and storing
/// the HTLC's shared secret. Only another HTLC (another channel or id) carrying a recorded HMAC is a replay.
/// </para>
/// <para>
/// Expiry: an entry is kept while the chain has not passed <c>expiryHeight</c>, the incoming HTLC's
/// <c>cltv_expiry</c> (as LND's decaying log). Once the chain is past it, a replay of the onion can no longer be
/// forwarded (its <c>outgoing_cltv_value</c> is below the incoming expiry, so it is in the past) and a final-hop replay
/// meets an invoice already paid or failed. Implementations decide when to forget: the persistent store prunes lazily
/// inside <see cref="TryAddAsync"/> (at most once per new chain height, from the chain monitor's last processed
/// height), the in-memory one evicts the soonest-expiring entry when full. <see cref="PruneAsync"/> is an optional
/// explicit prune; nothing in the node has to call it. A node that receives no HTLCs does not prune, which is harmless:
/// the set only holds entries of HTLCs it already received.
/// </para>
/// <para>
/// Persistence (migration <c>AddOnionReplaySet</c>): one row per HMAC, <c>OnionReplayEntries(Hmac primary key,
/// ChannelId, HtlcId, ExpiryHeight)</c> with an index on <c>ExpiryHeight</c>, through
/// <see cref="IOnionReplayDbRepository"/>. A new entry is durable when <see cref="TryAddAsync"/> returns, so before the
/// HTLC is forwarded or fulfilled.
/// </para>
/// </remarks>
public interface IOnionReplayStore
{
    /// <summary>
    /// Records <paramref name="hmac"/> for the incoming HTLC <paramref name="channelId"/>/<paramref name="htlcId"/>,
    /// unless another HTLC recorded it first.
    /// </summary>
    /// <param name="hmac">The 32-byte packet HMAC of the incoming onion (after a successful peel).</param>
    /// <param name="channelId">The incoming channel.</param>
    /// <param name="htlcId">The incoming HTLC's id on <paramref name="channelId"/>.</param>
    /// <param name="expiryHeight">The incoming HTLC's <c>cltv_expiry</c>: the entry is kept until the chain passes
    /// it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>True when the HMAC is new, or was recorded by this same HTLC; false when it is a replay.</returns>
    /// <exception cref="ArgumentException"><paramref name="hmac"/> is not 32 bytes long.</exception>
    Task<bool> TryAddAsync(ReadOnlyMemory<byte> hmac, ChannelId channelId, ulong htlcId, uint expiryHeight,
                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets every entry whose <c>expiryHeight</c> is below <paramref name="blockHeight"/> (the chain passed it).
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    Task<int> PruneAsync(uint blockHeight, CancellationToken cancellationToken = default);
}