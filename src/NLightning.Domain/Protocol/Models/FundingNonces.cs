namespace NLightning.Domain.Protocol.Models;

using Bitcoin.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;

/// <summary>
/// The <c>next_local_nonces</c> map of simple taproot channels (TLV 22 of <c>revoke_and_ack</c> and
/// <c>channel_reestablish</c>): one MuSig2 verification nonce per active funding transaction, keyed by its txid.
/// </summary>
/// <remarks>
/// <para>On the wire each entry is [<c>32*byte</c>:<c>funding_txid</c>] [<c>66*byte</c>:<c>public_nonce</c>]
/// (<see cref="EntryLength"/> bytes). The txid is in internal (serialized) byte order, the order <see cref="TxId"/>
/// holds and <c>funding_created</c> writes, as LND 0.21 (<c>lnwire/local_nonces.go</c>) and Eclair 0.14.3 do.</para>
/// <para>The entries are kept sorted by the txid bytes (unsigned, lexicographic), the order LND writes them in, so
/// <see cref="Entries"/> and the encoding are canonical whatever order they were given or read in; the spec does not
/// require an order, so a peer's unsorted map is accepted. A map holds at most <see cref="MaxEntries"/> entries (LND's
/// limit) and never two for the same txid.</para>
/// </remarks>
public sealed class FundingNonces : IEquatable<FundingNonces>
{
    /// <summary>The most entries a map may hold (LND 0.21 refuses more).</summary>
    public const int MaxEntries = 16;

    /// <summary>The size of one entry: a 32-byte txid and a 66-byte public nonce.</summary>
    public const int EntryLength = CryptoConstants.Sha256HashLen + MusigConstants.PublicNonceLen;

    private readonly (TxId FundingTxId, MusigPublicNonce Nonce)[] _entries;

    /// <summary>The entries, sorted by the txid bytes.</summary>
    public IReadOnlyList<(TxId FundingTxId, MusigPublicNonce Nonce)> Entries => _entries;

    /// <summary>The number of entries.</summary>
    public int Count => _entries.Length;

    /// <exception cref="ArgumentException">More than <see cref="MaxEntries"/> entries, or a txid twice.</exception>
    public FundingNonces(IEnumerable<(TxId FundingTxId, MusigPublicNonce Nonce)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var sorted = entries.ToArray();
        if (sorted.Length > MaxEntries)
            throw new ArgumentException($"next_local_nonces holds at most {MaxEntries} entries, not {sorted.Length}",
                                        nameof(entries));

        Array.Sort(sorted, static (a, b) => Compare(a.FundingTxId, b.FundingTxId));
        for (var i = 1; i < sorted.Length; i++)
        {
            if (Compare(sorted[i - 1].FundingTxId, sorted[i].FundingTxId) == 0)
                throw new ArgumentException($"next_local_nonces holds funding txid {sorted[i].FundingTxId} twice",
                                            nameof(entries));
        }

        _entries = sorted;
    }

    /// <summary>A map with one entry.</summary>
    public static FundingNonces Single(TxId fundingTxId, MusigPublicNonce nonce) => new([(fundingTxId, nonce)]);

    /// <summary>The nonce for <paramref name="fundingTxId"/>, if the map has one.</summary>
    public bool TryGetNonce(TxId fundingTxId, out MusigPublicNonce nonce)
    {
        foreach (var (entryTxId, entryNonce) in _entries)
        {
            if (entryTxId != fundingTxId)
                continue;

            nonce = entryNonce;
            return true;
        }

        nonce = default;
        return false;
    }

    /// <summary>Whether the map has a nonce for <paramref name="fundingTxId"/>.</summary>
    public bool Contains(TxId fundingTxId) => TryGetNonce(fundingTxId, out _);

    /// <summary>The wire encoding: every entry, txid (internal order) then nonce, in <see cref="Entries"/> order.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[_entries.Length * EntryLength];
        for (var i = 0; i < _entries.Length; i++)
        {
            var offset = i * EntryLength;
            ((ReadOnlySpan<byte>)_entries[i].FundingTxId).CopyTo(bytes.AsSpan(offset));
            ((ReadOnlySpan<byte>)_entries[i].Nonce).CopyTo(bytes.AsSpan(offset + CryptoConstants.Sha256HashLen));
        }

        return bytes;
    }

    public bool Equals(FundingNonces? other)
    {
        if (other is null)
            return false;

        return ReferenceEquals(this, other) || _entries.AsSpan().SequenceEqual(other._entries);
    }

    public override bool Equals(object? obj) => obj is FundingNonces other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in _entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }

    private static int Compare(TxId left, TxId right) =>
        ((ReadOnlySpan<byte>)left).SequenceCompareTo(right);
}