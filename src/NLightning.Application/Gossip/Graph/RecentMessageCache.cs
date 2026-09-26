using System.Security.Cryptography;

namespace NLightning.Application.Gossip.Graph;

/// <summary>
/// The hashes of the last gossip messages the ingress settled (plan BOLT7 §3.3 stage 2): an exact copy of one of them
/// (the same message relayed by another peer) is dropped before any signature or chain work. Bounded, oldest evicted
/// first. Thread-safe.
/// </summary>
public sealed class RecentMessageCache
{
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly HashSet<UInt128> _set = [];
    private readonly Queue<UInt128> _order = new();

    /// <param name="capacity">Hashes kept; 0 disables the cache.</param>
    public RecentMessageCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    /// <summary>The number of hashes kept.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
                return _set.Count;
        }
    }

    /// <summary>True when a message with these type and payload bytes was added and not evicted yet.</summary>
    public bool Contains(ushort type, ReadOnlySpan<byte> payload)
    {
        var key = Key(type, payload);
        lock (_lock)
            return _set.Contains(key);
    }

    /// <summary>Remembers a message; false when it was already known.</summary>
    public bool Add(ushort type, ReadOnlySpan<byte> payload)
    {
        if (_capacity == 0)
            return true;

        var key = Key(type, payload);
        lock (_lock)
        {
            if (!_set.Add(key))
                return false;

            _order.Enqueue(key);
            while (_order.Count > _capacity)
                _set.Remove(_order.Dequeue());

            return true;
        }
    }

    /// <summary>
    /// The first 128 bits of SHA-256 over the type and the payload: collisions need a second preimage, so a peer
    /// cannot make us drop someone else's message.
    /// </summary>
    private static UInt128 Key(ushort type, ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> typeBytes = [(byte)(type >> 8), (byte)type];
        hash.AppendData(typeBytes);
        hash.AppendData(payload);
        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return new UInt128(BitConverter.ToUInt64(digest), BitConverter.ToUInt64(digest[8..]));
    }
}