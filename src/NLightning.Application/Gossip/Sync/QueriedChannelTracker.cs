using System.Collections.Concurrent;

namespace NLightning.Application.Gossip.Sync;

using Domain.Channels.ValueObjects;

/// <summary>
/// The unknown channels a range sync asked one peer for with <c>query_short_channel_ids</c> (NL-415), shared by every
/// sync session: while a claim holds, another session's per-batch re-diff skips the channel, since its announcement
/// is already on its way (the answer is queued in the ingress or waits for its chain lookup, which with the chain check
/// takes far longer than the peers take to answer).
/// </summary>
/// <remarks>
/// A claim is taken when the batch is built (<see cref="TryClaim"/>, atomically, so two sessions building a batch at
/// once never both take a channel), renewed when the peer answered (<see cref="Renew"/>: the time counts from the
/// answer, not from the wait for ingress room before the query), dropped when the query failed
/// (<see cref="Release"/>: the peer may never answer it, so another peer may be asked at once) and ends by itself after
/// the time to live. What the ingress drops or gives up on comes back through the missed-channel retry, which asks
/// again regardless of claims. A peer may also answer with <c>reply_short_channel_ids_end</c> without sending some of
/// the announcements (<c>full_information</c> = 0, a pruned or lazy peer): a claim taken with <c>recycle</c> hands its
/// channel to <see cref="Prune"/>'s caller when it ends, so the missed-channel retry asks another peer once
/// (<see cref="Expire"/> ends a batch's claims at once when the peer said it lacks full information). Thread-safe.
/// </remarks>
internal sealed class QueriedChannelTracker
{
    private readonly ConcurrentDictionary<ShortChannelId, Claim> _claims = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeToLive;
    private readonly int _maxClaims;

    /// <param name="timeProvider">The clock.</param>
    /// <param name="timeToLive">How long a claim holds after it was taken or renewed (zero: no claims at all).</param>
    /// <param name="maxClaims">At most this many claims (beyond it channels are not claimed, only asked for).</param>
    public QueriedChannelTracker(TimeProvider timeProvider, TimeSpan timeToLive, int maxClaims = 500_000)
    {
        _timeProvider = timeProvider;
        _timeToLive = timeToLive;
        _maxClaims = maxClaims;
    }

    /// <summary>The claims held, ended ones included until the next <see cref="Prune"/> (tests, describegraph).</summary>
    public int Count => _claims.Count;

    /// <summary>True while another owner than <paramref name="owner"/> holds a claim on the channel.</summary>
    public bool IsClaimedByOther(ShortChannelId shortChannelId, object owner) =>
        _claims.TryGetValue(shortChannelId, out var claim) && !ReferenceEquals(claim.Owner, owner)
                                                           && claim.ExpiresAt > Now;

    /// <summary>
    /// Claims the channel for <paramref name="owner"/>: true when it was free, ended, or already the owner's; false
    /// while another owner holds it. Always true (nothing recorded) when claims are off or the tracker is full.
    /// </summary>
    /// <param name="shortChannelId">The channel.</param>
    /// <param name="owner">The sync session.</param>
    /// <param name="recycle">
    /// Hand the channel to <see cref="Prune"/>'s caller when the claim ends (a range sync batch; the missed-channel
    /// retry's own claims are not recycled, so a channel no peer delivers is asked for again once, not forever).
    /// </param>
    public bool TryClaim(ShortChannelId shortChannelId, object owner, bool recycle = false)
    {
        if (_timeToLive <= TimeSpan.Zero)
            return true;

        var now = Now;
        var mine = new Claim(owner, now + _timeToLive, recycle);
        while (true)
        {
            if (_claims.TryGetValue(shortChannelId, out var existing))
            {
                if (!ReferenceEquals(existing.Owner, owner) && existing.ExpiresAt > now)
                    return false;
                if (_claims.TryUpdate(shortChannelId, mine, existing))
                    return true;
                continue;
            }

            if (_claims.Count >= _maxClaims)
                return true;
            if (_claims.TryAdd(shortChannelId, mine))
                return true;
        }
    }

    /// <summary>Restarts the time to live of <paramref name="owner"/>'s claims on these channels.</summary>
    public void Renew(IEnumerable<ShortChannelId> shortChannelIds, object owner)
    {
        if (_timeToLive <= TimeSpan.Zero)
            return;

        var expiresAt = Now + _timeToLive;
        foreach (var shortChannelId in shortChannelIds)
        {
            if (_claims.TryGetValue(shortChannelId, out var existing) && ReferenceEquals(existing.Owner, owner))
                _claims.TryUpdate(shortChannelId, existing with { ExpiresAt = expiresAt }, existing);
        }
    }

    /// <summary>
    /// Ends <paramref name="owner"/>'s claims on these channels now but keeps them for <see cref="Prune"/>, which
    /// recycles the recyclable ones (the peer answered without full information: another peer may be asked at once,
    /// and what it did not send comes back through the missed-channel retry).
    /// </summary>
    public void Expire(IEnumerable<ShortChannelId> shortChannelIds, object owner)
    {
        var now = Now;
        foreach (var shortChannelId in shortChannelIds)
        {
            if (_claims.TryGetValue(shortChannelId, out var existing) && ReferenceEquals(existing.Owner, owner))
                _claims.TryUpdate(shortChannelId, existing with { ExpiresAt = now }, existing);
        }
    }

    /// <summary>Drops <paramref name="owner"/>'s claims on these channels (its query failed).</summary>
    public void Release(IEnumerable<ShortChannelId> shortChannelIds, object owner)
    {
        foreach (var shortChannelId in shortChannelIds)
        {
            if (_claims.TryGetValue(shortChannelId, out var existing) && ReferenceEquals(existing.Owner, owner))
                _claims.TryRemove(new KeyValuePair<ShortChannelId, Claim>(shortChannelId, existing));
        }
    }

    /// <summary>
    /// Drops the ended claims; returns how many. The channels of the ended recyclable claims are added to
    /// <paramref name="recycled"/> when given (the caller asks again for those still not in the graph).
    /// </summary>
    public int Prune(ICollection<ShortChannelId>? recycled = null)
    {
        var now = Now;
        var pruned = 0;
        foreach (var (shortChannelId, claim) in _claims)
        {
            if (claim.ExpiresAt > now
             || !_claims.TryRemove(new KeyValuePair<ShortChannelId, Claim>(shortChannelId, claim)))
                continue;

            pruned++;
            if (claim.Recycle)
                recycled?.Add(shortChannelId);
        }

        return pruned;
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private sealed record Claim(object Owner, DateTimeOffset ExpiresAt, bool Recycle = false);
}