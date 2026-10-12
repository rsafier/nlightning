namespace NLightning.Domain.Protocol.Models;

using Tlv;
using ValueObjects;

/// <summary>
/// A series of (possibly zero) TLVs
/// </summary>
/// <remarks>
/// The records are kept in insertion order. BOLT 1 requires strictly increasing types on the wire, so the stream
/// serializer validates the order when writing: a hand-built stream whose records are not in ascending type order
/// fails serialization instead of being silently re-sorted.
/// </remarks>
public sealed class TlvStream
{
    private readonly List<BaseTlv> _tlvs = [];

    /// <summary>
    /// Add a TLV to the stream
    /// </summary>
    /// <param name="baseTlv">The TLV to add</param>
    public void Add(BaseTlv baseTlv)
    {
        if (_tlvs.Any(t => t.Type == baseTlv.Type))
        {
            throw new ArgumentException($"A TLV with type {baseTlv.Type} already exists.");
        }

        _tlvs.Add(baseTlv);
    }

    /// <summary>
    /// Add a series of TLV to the stream
    /// </summary>
    /// <param name="tlvs">The TLVs to add</param>
    public void Add(params BaseTlv?[] tlvs)
    {
        foreach (var tlv in tlvs)
        {
            if (tlv is null)
                continue;

            Add(tlv);
        }
    }

    /// <summary>
    /// Get all TLVs in the stream, in insertion order
    /// </summary>
    public IEnumerable<BaseTlv> GetTlvs()
    {
        return _tlvs;
    }

    /// <summary>
    /// Get a specific TLV from the stream
    /// </summary>
    /// <param name="type">The type of TLV to get</param>
    /// <param name="tlv">The TLV to get</param>
    /// <returns></returns>
    public bool TryGetTlv(BigSize type, out BaseTlv? tlv)
    {
        tlv = _tlvs.FirstOrDefault(t => t.Type == type);
        return tlv is not null;
    }

    /// <summary>
    /// Check if any TLVs are present
    /// </summary>
    public bool Any()
    {
        return _tlvs.Count != 0;
    }
}