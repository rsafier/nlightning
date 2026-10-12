namespace NLightning.Domain.Protocol.OnionMessages;

/// <summary>
/// One raw <c>onionmsg_tlv</c> record: its type and value bytes (BOLT 4 "Onion Messages").
/// </summary>
/// <param name="Type">The TLV type.</param>
/// <param name="Value">The value, as on the wire.</param>
public sealed record OnionMessageTlvRecord(ulong Type, ReadOnlyMemory<byte> Value);