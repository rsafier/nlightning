namespace NLightning.Infrastructure.Protocol.Onion;

using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;

/// <summary>
/// A thread-safe, capacity-bounded, in-memory <see cref="IOnionReplayCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// When the cache is full, the oldest HMAC is evicted (FIFO), so a replay older than <see cref="Capacity"/> newer
/// packets is no longer detected, and nothing survives a restart. A persistent store (keyed together with the HTLC's
/// payment_hash and expiry) is needed before relying on it for funds.
/// </para>
/// <para>
/// FIFO eviction is only safe if every entry is authenticated: callers must record an HMAC only after the peel
/// verified it (see <see cref="IOnionReplayCache"/>). Even then an entry can be evicted before its HTLC could expire;
/// the M4 HTLC switch should key or expire entries by the HTLC's <c>cltv_expiry</c> instead.
/// </para>
/// <para>
/// HMACs are keyed as hex strings, whose hash codes are randomized per process, so a peer cannot craft colliding
/// HMACs to degrade lookups.
/// </para>
/// </remarks>
public sealed class OnionReplayCache : IOnionReplayCache
{
    /// <summary>
    /// The default number of HMACs kept.
    /// </summary>
    public const int DefaultCapacity = 100_000;

    private readonly Lock _lock = new();
    private readonly HashSet<string> _seen;
    private readonly Queue<string> _insertionOrder;

    /// <summary>
    /// The maximum number of HMACs kept.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// The number of HMACs currently kept.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _seen.Count;
        }
    }

    public OnionReplayCache() : this(DefaultCapacity)
    { }

    /// <param name="capacity">The maximum number of HMACs kept; must be positive.</param>
    public OnionReplayCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
        _seen = new HashSet<string>(StringComparer.Ordinal);
        _insertionOrder = new Queue<string>();
    }

    /// <inheritdoc />
    public bool TryAdd(ReadOnlySpan<byte> hmac)
    {
        if (hmac.Length != OnionConstants.HmacLength)
            throw new ArgumentException($"Onion HMAC must be {OnionConstants.HmacLength} bytes.", nameof(hmac));

        var key = Convert.ToHexString(hmac);

        lock (_lock)
        {
            if (!_seen.Add(key))
                return false;

            _insertionOrder.Enqueue(key);
            if (_insertionOrder.Count > Capacity)
                _seen.Remove(_insertionOrder.Dequeue());

            return true;
        }
    }
}