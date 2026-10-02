using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.Accounting.Financial;

using Books;
using Services;

/// <summary>
/// The digest of a period close (A3-T5, D-A13): SHA-256 over a domain tag, the close's header (period, bounds, last
/// ledger sequence, the feed's chain hash there, the previous close's digest, the node id, forced, closed at), the hash
/// of every financial entry of the period (ledger and adjustment order), of every lot relief of the period (id order),
/// of every lot open at the period's end with its remaining amount then (id order), the counts and the closing state's
/// text. The node key signs the digest (<c>ILightningSigner.SignNodeMessage</c>), so anyone with the node id can check
/// a close, and <c>nltg accounting verify</c> recomputes it from the stored rows.
/// </summary>
/// <remarks>
/// <para>What a later legitimate write changes is left out, everything else is covered: an entry's
/// <c>ClosedPeriodId</c> (the selector itself; an entry moved out of the period changes the set), a lot's current
/// <c>RemainingMsat</c> (the remaining amount at the end is covered instead), its <c>Account</c> (a transfer after the
/// close may move it) and its <c>ClosedPeriodId</c>.</para>
/// <para>The preimage starts with the tag and is far longer than 32 bytes, so the digest can never be the inner
/// SHA-256 of a BOLT 7 message's double SHA-256: the node key's signature of it cannot be replayed as a gossip
/// signature.</para>
/// <para>Integers are big-endian, strings UTF-8 with a 4-byte length, optional fields carry a presence byte, times are
/// UTC ticks, fiat amounts their canonical text (<see cref="AccountingClosingState.FormatFiat"/>, so a provider that
/// pads the scale changes nothing).</para>
/// </remarks>
public sealed class AccountingCloseDigest : IDisposable
{
    /// <summary>The domain tag at the start of every preimage.</summary>
    public const string Tag = "nltg/accounting/period-close/v1";

    private const byte EntryMarker = (byte)'E';
    private const byte ReliefMarker = (byte)'R';
    private const byte LotMarker = (byte)'L';
    private const byte StateMarker = (byte)'S';

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _entries;
    private long _reliefs;
    private long _lots;
    private bool _finished;

    /// <summary>Starts a digest with the close's header.</summary>
    public AccountingCloseDigest(AccountingCloseHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        using var stream = new MemoryStream();
        WriteString(stream, Tag);
        WriteString(stream, header.PeriodId);
        WriteInt64(stream, header.Start.UtcTicks);
        WriteInt64(stream, header.End.UtcTicks);
        WriteInt64(stream, header.LastLedgerSeq);
        WriteFixed(stream, header.ChainHash, AccountingEventHasher.HashLength, nameof(header.ChainHash));
        WriteFixed(stream, header.PreviousDigest ?? new byte[AccountingEventHasher.HashLength],
                   AccountingEventHasher.HashLength, nameof(header.PreviousDigest));
        WriteBytes(stream, header.NodeId);
        stream.WriteByte(header.Forced ? (byte)1 : (byte)0);
        WriteInt64(stream, header.ClosedAt.UtcTicks);
        _hash.AppendData(stream.ToArray());
    }

    /// <summary>How many entries, reliefs and open lots went in.</summary>
    public (long Entries, long Reliefs, long Lots) Counts => (_entries, _reliefs, _lots);

    /// <summary>Adds a financial entry of the period (in ledger, then adjustment order).</summary>
    public void AddEntry(AccountingEntry entry)
    {
        EnsureOpen();
        _hash.AppendData([EntryMarker]);
        _hash.AppendData(SHA256.HashData(GetEntryBytes(entry)));
        _entries++;
    }

    /// <summary>Adds a lot relief of the period (in id order).</summary>
    public void AddRelief(AccountingLotRelief relief)
    {
        EnsureOpen();
        _hash.AppendData([ReliefMarker]);
        _hash.AppendData(SHA256.HashData(GetReliefBytes(relief)));
        _reliefs++;
    }

    /// <summary>Adds a lot open at the period's end with its remaining amount then (in id order).</summary>
    public void AddOpenLot(AccountingLot lot, long remainingAtEndMsat)
    {
        EnsureOpen();
        _hash.AppendData([LotMarker]);
        _hash.AppendData(SHA256.HashData(GetLotBytes(lot, remainingAtEndMsat)));
        _lots++;
    }

    /// <summary>Adds the counts and the closing state's stored text and returns the 32-byte digest.</summary>
    public byte[] Finish(string closingState)
    {
        ArgumentNullException.ThrowIfNull(closingState);
        EnsureOpen();
        _finished = true;

        using var stream = new MemoryStream();
        stream.WriteByte(StateMarker);
        WriteInt64(stream, _entries);
        WriteInt64(stream, _reliefs);
        WriteInt64(stream, _lots);
        WriteString(stream, closingState);
        _hash.AppendData(stream.ToArray());
        return _hash.GetHashAndReset();
    }

    public void Dispose() => _hash.Dispose();

    /// <summary>The canonical bytes of a financial entry (its <c>ClosedPeriodId</c> left out).</summary>
    public static byte[] GetEntryBytes(AccountingEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var stream = new MemoryStream();
        stream.WriteByte(1);
        stream.WriteByte((byte)entry.Book);
        WriteInt64(stream, entry.LedgerSeq);
        WriteInt32(stream, entry.Adjustment);
        WriteString(stream, entry.EventKey);
        WriteInt32(stream, (int)entry.Kind);
        WriteInt64(stream, entry.OccurredAt.UtcTicks);
        WriteOptionalBytes(stream, entry.ChannelId is { } channelId ? (byte[])channelId : null);
        WriteOptionalBytes(stream, entry.PaymentHash is { } hash ? (byte[])hash : null);
        WriteOptionalString(stream, entry.Note);
        WriteInt32(stream, (int)entry.Flags);
        WriteOptionalInt64(stream, entry.Classification is { } classification ? (long)classification : null);
        WriteOptionalInt64(stream, entry.RuleId);
        WriteInt32(stream, entry.Postings.Count);
        foreach (var posting in entry.Postings)
        {
            WriteInt32(stream, (int)posting.Account);
            WriteOptionalString(stream, posting.AccountName);
            WriteInt64(stream, posting.AmountMsat);
            WriteOptionalString(stream, posting.FiatAmount is { } fiat ? AccountingClosingState.FormatFiat(fiat) : null);
            WriteOptionalString(stream, posting.FiatCurrency);
            WriteOptionalInt64(stream, posting.PriceId);
        }

        return stream.ToArray();
    }

    /// <summary>The canonical bytes of a lot relief (its <c>ClosedPeriodId</c> left out).</summary>
    public static byte[] GetReliefBytes(AccountingLotRelief relief)
    {
        ArgumentNullException.ThrowIfNull(relief);
        using var stream = new MemoryStream();
        stream.WriteByte(1);
        WriteInt64(stream, relief.Id);
        WriteInt64(stream, relief.LotId);
        WriteInt64(stream, relief.LedgerSeq);
        WriteInt32(stream, relief.Adjustment);
        WriteInt64(stream, relief.RelievedAt.UtcTicks);
        WriteInt64(stream, relief.Msat);
        WriteOptionalString(stream, relief.FiatCostRelieved is { } cost ? AccountingClosingState.FormatFiat(cost) : null);
        WriteOptionalString(stream, relief.Proceeds is { } proceeds ? AccountingClosingState.FormatFiat(proceeds) : null);

        // A move or a settlement (NL-657); a disposal adds nothing, so the closes made before stay verifiable
        if (relief.Kind != AccountingLotReliefKind.Disposal)
        {
            stream.WriteByte(2);
            stream.WriteByte((byte)relief.Kind);
        }

        return stream.ToArray();
    }

    /// <summary>The canonical bytes of a lot open at a close (its current remaining amount and <c>ClosedPeriodId</c>
    /// left out; the remaining amount at the close in; the bucket, the acquisition time of a moved part and a debt's
    /// lender only when one is set, NL-657).</summary>
    public static byte[] GetLotBytes(AccountingLot lot, long remainingAtEndMsat)
    {
        ArgumentNullException.ThrowIfNull(lot);
        using var stream = new MemoryStream();
        stream.WriteByte(1);
        WriteInt64(stream, lot.Id);
        WriteInt64(stream, lot.AcquiredAt.UtcTicks);
        stream.WriteByte((byte)lot.Origin);
        WriteOptionalInt64(stream, lot.SourceLedgerSeq);
        WriteInt32(stream, lot.SourceAdjustment);
        WriteOptionalInt64(stream, lot.ParentLotId);
        WriteInt64(stream, lot.OriginalMsat);
        WriteInt64(stream, remainingAtEndMsat);
        WriteOptionalString(stream, lot.FiatCost is { } cost ? AccountingClosingState.FormatFiat(cost) : null);
        WriteOptionalString(stream, lot.FiatCurrency);
        WriteOptionalInt64(stream, lot.PriceId);
        stream.WriteByte(lot.BasisEstimated ? (byte)1 : (byte)0);

        // The bucket, a moved part's acquisition time and a debt's lender (NL-657); a lot of the node-wide pool adds
        // nothing, so the closes made before stay verifiable
        if (lot.Bucket is not null || lot.HeldSince is not null || lot.Lender is not null)
        {
            stream.WriteByte(2);
            WriteOptionalInt64(stream, lot.Bucket is { } bucket ? (long)bucket : null);
            WriteOptionalInt64(stream, lot.HeldSince?.UtcTicks);
            WriteOptionalInt64(stream, lot.Lender is { } lender ? (long)lender : null);
        }

        return stream.ToArray();
    }

    private void EnsureOpen()
    {
        if (_finished)
            throw new InvalidOperationException("The digest is finished");
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

    private static void WriteOptionalInt64(Stream stream, long? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is { } v)
            WriteInt64(stream, v);
    }

    private static void WriteBytes(Stream stream, byte[] value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteFixed(Stream stream, byte[]? value, int length, string name)
    {
        if (value is null || value.Length != length)
            throw new ArgumentException($"{name} must be {length} bytes", name);

        stream.Write(value);
    }

    private static void WriteOptionalBytes(Stream stream, byte[]? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
            WriteBytes(stream, value);
    }

    private static void WriteString(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteOptionalString(Stream stream, string? value) =>
        WriteOptionalBytes(stream, value is null ? null : Encoding.UTF8.GetBytes(value));
}

/// <summary>The header of a period close's digest (<see cref="AccountingCloseDigest"/>).</summary>
/// <param name="PeriodId">The period.</param>
/// <param name="Start">Its first instant.</param>
/// <param name="End">The first instant after it.</param>
/// <param name="LastLedgerSeq">The financial book's cursor at the close (the last feed sequence it covers).</param>
/// <param name="ChainHash">The feed's chain hash at <paramref name="LastLedgerSeq"/> (32 zero bytes at 0).</param>
/// <param name="PreviousDigest">The digest of the previous close, or null for the first one (32 zero bytes).</param>
/// <param name="NodeId">Our node id (33 bytes), whose key signs the digest.</param>
/// <param name="Forced">Closed with <c>--force</c>.</param>
/// <param name="ClosedAt">When it was closed.</param>
public sealed record AccountingCloseHeader(
    string PeriodId,
    DateTimeOffset Start,
    DateTimeOffset End,
    long LastLedgerSeq,
    byte[] ChainHash,
    byte[]? PreviousDigest,
    byte[] NodeId,
    bool Forced,
    DateTimeOffset ClosedAt);