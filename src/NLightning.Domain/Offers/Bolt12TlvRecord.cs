namespace NLightning.Domain.Offers;

/// <summary>
/// One raw record of a BOLT 12 TLV stream: its type and value bytes, as on the wire.
/// </summary>
/// <param name="Type">The TLV type.</param>
/// <param name="Value">The value bytes.</param>
public sealed record Bolt12TlvRecord(ulong Type, ReadOnlyMemory<byte> Value);