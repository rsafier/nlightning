namespace NLightning.Infrastructure.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;

/// <summary>
/// A thread-safe, in-memory <see cref="IOnionReplayStore"/>: entries keyed by HMAC, owned by the incoming HTLC that
/// recorded them and expired by block height (NL-078).
/// </summary>
/// <remarks>
/// <para>
/// Nothing survives a restart: a persistent implementation of <see cref="IOnionReplayStore"/> (a table, see the
/// interface remarks) replaces it once the schema exists. Until then the switch's re-processing of stored HTLCs is
/// covered by the owner check, and only onions of HTLCs forgotten across a restart are missed.
/// </para>
/// <para>
/// The size is bounded by <see cref="Capacity"/> as a last resort against memory exhaustion. When full, the entry that
/// expires first is dropped (the one whose replay is the least likely to still be usable), never the newest.
/// </para>
/// <para>
/// HMACs are keyed as hex strings, whose hash codes are randomized per process, so a peer cannot craft colliding HMACs
/// to degrade lookups.
/// </para>
/// </remarks>
public sealed class InMemoryOnionReplayStore : IOnionReplayStore
{
    /// <summary>The default maximum number of entries.</summary>
    public const int DefaultCapacity = 1_000_000;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SortedSet<(uint ExpiryHeight, string Key)> _byExpiry = new();
    private readonly Lock _lock = new();

    public InMemoryOnionReplayStore() : this(DefaultCapacity)
    { }

    /// <param name="capacity">The maximum number of entries; must be positive.</param>
    public InMemoryOnionReplayStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
    }

    /// <summary>The maximum number of entries kept.</summary>
    public int Capacity { get; }

    /// <summary>The number of entries kept now.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <inheritdoc />
    public Task<bool> TryAddAsync(ReadOnlyMemory<byte> hmac, ChannelId channelId, ulong htlcId, uint expiryHeight,
                                  CancellationToken cancellationToken = default)
    {
        if (hmac.Length != OnionConstants.HmacLength)
            throw new ArgumentException($"Onion HMAC must be {OnionConstants.HmacLength} bytes.", nameof(hmac));

        cancellationToken.ThrowIfCancellationRequested();
        var key = Convert.ToHexString(hmac.Span);

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing))
                return Task.FromResult(existing.ChannelId == channelId && existing.HtlcId == htlcId);

            if (_entries.Count >= Capacity)
            {
                var first = _byExpiry.Min;
                _byExpiry.Remove(first);
                _entries.Remove(first.Key);
            }

            _entries.Add(key, new Entry(channelId, htlcId, expiryHeight));
            _byExpiry.Add((expiryHeight, key));
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(uint blockHeight, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var removed = 0;
        lock (_lock)
        {
            while (_byExpiry.Count > 0 && _byExpiry.Min.ExpiryHeight < blockHeight)
            {
                var first = _byExpiry.Min;
                _byExpiry.Remove(first);
                _entries.Remove(first.Key);
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    private readonly record struct Entry(ChannelId ChannelId, ulong HtlcId, uint ExpiryHeight);
}