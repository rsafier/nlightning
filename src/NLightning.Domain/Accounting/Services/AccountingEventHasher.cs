using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.Accounting.Services;

using Models;

/// <summary>
/// The canonical bytes of a sealed accounting event and its chain hash: SHA-256(previous hash || canonical bytes).
/// </summary>
/// <remarks>
/// The encoding is versioned (first byte) and covers every field a reader relies on, the ledger sequence included, so
/// a row edited, removed or reordered after sealing breaks the chain from that row on. Integers are big-endian,
/// strings are UTF-8 with a 4-byte length, optional fields carry a presence byte, and the details are written in
/// ordinal key order.
/// </remarks>
public static class AccountingEventHasher
{
    public const int HashLength = 32;
    private const byte EncodingVersion = 1;

    /// <summary>
    /// The chain hash of <paramref name="accountingEvent"/> sealed at <paramref name="ledgerSeq"/> after
    /// <paramref name="previousHash"/>.
    /// </summary>
    public static byte[] ComputeHash(ReadOnlySpan<byte> previousHash, long ledgerSeq,
                                     AccountingEventModel accountingEvent)
    {
        if (previousHash.Length != HashLength)
            throw new ArgumentException($"The previous hash must be {HashLength} bytes", nameof(previousHash));

        var canonical = GetCanonicalBytes(ledgerSeq, accountingEvent);
        var buffer = new byte[HashLength + canonical.Length];
        previousHash.CopyTo(buffer);
        canonical.CopyTo(buffer, HashLength);
        return SHA256.HashData(buffer);
    }

    /// <summary>
    /// The canonical bytes of <paramref name="accountingEvent"/> at <paramref name="ledgerSeq"/>.
    /// </summary>
    public static byte[] GetCanonicalBytes(long ledgerSeq, AccountingEventModel accountingEvent)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);

        using var stream = new MemoryStream();
        stream.WriteByte(EncodingVersion);
        WriteInt64(stream, ledgerSeq);
        WriteString(stream, accountingEvent.EventKey);
        WriteInt32(stream, (int)accountingEvent.Kind);
        WriteInt64(stream, accountingEvent.OccurredAt.UtcTicks);
        WriteOptionalUInt32(stream, accountingEvent.BlockHeight);
        WriteOptionalBytes(stream, accountingEvent.ChannelId is { } channelId ? (byte[])channelId : null);
        WriteOptionalBytes(stream, accountingEvent.ShortChannelId is { } scid ? (byte[])scid : null);
        WriteOptionalBytes(stream, accountingEvent.PaymentHash is { } hash ? (byte[])hash : null);
        WriteOptionalBytes(stream, accountingEvent.TxId is { } txId ? (byte[])txId : null);
        WriteOptionalUInt32(stream, accountingEvent.OutputIndex);
        WriteOptionalBytes(stream, accountingEvent.Counterparty is { } node ? (byte[])node : null);
        WriteInt64(stream, accountingEvent.AmountMsat);
        WriteInt64(stream, accountingEvent.FeeMsat);
        stream.WriteByte((byte)accountingEvent.Finality);
        WriteInt32(stream, (int)accountingEvent.Flags);

        var details = accountingEvent.Details.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        WriteInt32(stream, details.Count);
        foreach (var (key, value) in details)
        {
            WriteString(stream, key);
            WriteString(stream, value);
        }

        return stream.ToArray();
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteOptionalUInt32(Stream stream, uint? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is null)
            return;

        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value.Value);
        stream.Write(buffer);
    }

    private static void WriteOptionalBytes(Stream stream, byte[]? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is null)
            return;

        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }
}