using System.Buffers.Binary;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Channels.ValueObjects;
using Domain.Payments.Models;

/// <summary>
/// The <c>Invoices.Htlcs</c> column (NL-1167): version 1, a big-endian u16 count, then per HTLC its short channel id
/// (8), htlc id (8), amount msat (8), accept height (4), accept time and resolve time as UTC ticks (8 each, 0 = none),
/// cltv expiry (4), state (1) and MPP total msat (8).
/// </summary>
internal static class InvoiceHtlcCodec
{
    private const byte Version = 1;
    private const int RecordLength = 8 + 8 + 8 + 4 + 8 + 8 + 4 + 1 + 8;

    public static byte[]? Encode(IReadOnlyList<InvoiceHtlc> htlcs)
    {
        if (htlcs.Count == 0)
            return null;

        var bytes = new byte[3 + htlcs.Count * RecordLength];
        bytes[0] = Version;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), checked((ushort)htlcs.Count));
        var span = bytes.AsSpan(3);
        foreach (var htlc in htlcs)
        {
            ((ReadOnlySpan<byte>)htlc.ShortChannelId).CopyTo(span);
            BinaryPrimitives.WriteUInt64BigEndian(span[8..], htlc.HtlcId);
            BinaryPrimitives.WriteUInt64BigEndian(span[16..], htlc.AmountMsat);
            BinaryPrimitives.WriteUInt32BigEndian(span[24..], htlc.AcceptHeight);
            BinaryPrimitives.WriteInt64BigEndian(span[28..], htlc.AcceptTime.UtcTicks);
            BinaryPrimitives.WriteInt64BigEndian(span[36..], htlc.ResolveTime?.UtcTicks ?? 0);
            BinaryPrimitives.WriteUInt32BigEndian(span[44..], htlc.ExpiryHeight);
            span[48] = (byte)htlc.State;
            BinaryPrimitives.WriteUInt64BigEndian(span[49..], htlc.MppTotalMsat);
            span = span[RecordLength..];
        }

        return bytes;
    }

    /// <summary>The records, or none for a null, empty or unknown-version column.</summary>
    public static IReadOnlyList<InvoiceHtlc> Decode(byte[]? bytes)
    {
        if (bytes is not { Length: >= 3 } || bytes[0] != Version)
            return [];

        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1));
        if (bytes.Length != 3 + count * RecordLength)
            return [];

        var htlcs = new List<InvoiceHtlc>(count);
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(3 + i * RecordLength, RecordLength);
            var resolveTicks = BinaryPrimitives.ReadInt64BigEndian(span[36..]);
            htlcs.Add(new InvoiceHtlc(new ShortChannelId(span[..8].ToArray()),
                                      BinaryPrimitives.ReadUInt64BigEndian(span[8..]),
                                      BinaryPrimitives.ReadUInt64BigEndian(span[16..]),
                                      BinaryPrimitives.ReadUInt32BigEndian(span[24..]),
                                      new DateTimeOffset(BinaryPrimitives.ReadInt64BigEndian(span[28..]), TimeSpan.Zero),
                                      resolveTicks == 0 ? null : new DateTimeOffset(resolveTicks, TimeSpan.Zero),
                                      BinaryPrimitives.ReadUInt32BigEndian(span[44..]), (InvoiceHtlcState)span[48],
                                      BinaryPrimitives.ReadUInt64BigEndian(span[49..])));
        }

        return htlcs;
    }
}