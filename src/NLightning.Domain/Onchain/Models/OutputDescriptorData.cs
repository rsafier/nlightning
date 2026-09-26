using System.Buffers.Binary;

namespace NLightning.Domain.Onchain.Models;

using Channels.Commitments;
using Channels.Enums;
using Crypto.Constants;
using Crypto.ValueObjects;

/// <summary>
/// What an <see cref="OutputResolutionModel"/> row stores in <see cref="OutputResolutionModel.DescriptorData"/>: the
/// facts of one output a resolver needs to spend it (BOLT 5 plan §3.3). Public keys and scripts only; private keys are
/// always re-derived by the signer, never stored.
/// </summary>
/// <remarks>
/// Encoding (version 1, big-endian): <c>version(1) || amount_sat(8) || csv_delay(2) || flags(1: 0x01 anchors, 0x02
/// point, 0x04 htlc, 0x08 witness script) || u16 len || scriptPubKey || [u16 len || witness script] || [point(33)] ||
/// [direction(1) || id(8) || amount_msat(8) || payment_hash(32) || cltv_expiry(4)]</c>. Written by the on-chain
/// channel watcher for every output of a classified commitment; resolvers may write it for outputs they add (second-level
/// outputs).
/// </remarks>
/// <param name="AmountSat">The output amount in satoshis.</param>
/// <param name="ScriptPubKey">The output script.</param>
/// <param name="WitnessScript">The P2WSH witness script; null for a P2WPKH output.</param>
/// <param name="CsvDelay">The relative delay of the output's spend path (0 when none).</param>
/// <param name="HasAnchors">Whether the commitment uses option_anchors.</param>
/// <param name="PerCommitmentPoint">The holder's per-commitment point of the commitment the output belongs to (ours for
/// our commitment, the peer's for its commitments), when known.</param>
/// <param name="Htlc">The HTLC of an HTLC output (direction from our point of view).</param>
public sealed record OutputDescriptorData(
    ulong AmountSat,
    byte[] ScriptPubKey,
    byte[]? WitnessScript,
    ushort CsvDelay,
    bool HasAnchors,
    CompactPubKey? PerCommitmentPoint,
    SpecHtlc? Htlc)
{
    /// <summary>The only encoding version so far.</summary>
    public const byte Version = 1;

    private const byte FlagAnchors = 0x01;
    private const byte FlagPoint = 0x02;
    private const byte FlagHtlc = 0x04;
    private const byte FlagWitnessScript = 0x08;
    private const int HtlcLength = 1 + 8 + 8 + CryptoConstants.Sha256HashLen + 4;

    /// <summary>The data of a descriptor of a mapped commitment.</summary>
    /// <param name="descriptor">The output.</param>
    /// <param name="perCommitmentPoint">The commitment's holder point (<see cref="CommitmentOutputMap.PerCommitmentPoint"/>).</param>
    public static OutputDescriptorData FromDescriptor(CommitmentOutputDescriptor descriptor,
                                                      CompactPubKey? perCommitmentPoint)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new OutputDescriptorData(descriptor.AmountSat, descriptor.ScriptPubKey, descriptor.WitnessScript,
                                        descriptor.CsvDelay, descriptor.HasAnchors, perCommitmentPoint,
                                        descriptor.Htlc);
    }

    /// <summary>Encodes it (see the remarks).</summary>
    public byte[] Encode()
    {
        ArgumentNullException.ThrowIfNull(ScriptPubKey);
        if (ScriptPubKey.Length > ushort.MaxValue || WitnessScript is { Length: > ushort.MaxValue })
            throw new InvalidOperationException("A script is too long to encode.");

        var flags = (byte)((HasAnchors ? FlagAnchors : 0) | (PerCommitmentPoint.HasValue ? FlagPoint : 0)
                                                          | (Htlc.HasValue ? FlagHtlc : 0)
                                                          | (WitnessScript is not null ? FlagWitnessScript : 0));
        var length = 1 + 8 + 2 + 1 + 2 + ScriptPubKey.Length
                   + (WitnessScript is null ? 0 : 2 + WitnessScript.Length)
                   + (PerCommitmentPoint.HasValue ? CryptoConstants.CompactPubkeyLen : 0)
                   + (Htlc.HasValue ? HtlcLength : 0);

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        span[0] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(span[1..], AmountSat);
        BinaryPrimitives.WriteUInt16BigEndian(span[9..], CsvDelay);
        span[11] = flags;
        var offset = 12;
        offset = WriteBytes(span, offset, ScriptPubKey);
        if (WitnessScript is not null)
            offset = WriteBytes(span, offset, WitnessScript);

        if (PerCommitmentPoint is { } point)
        {
            ((ReadOnlySpan<byte>)point).CopyTo(span[offset..]);
            offset += CryptoConstants.CompactPubkeyLen;
        }

        if (Htlc is { } htlc)
        {
            span[offset] = (byte)htlc.Direction;
            BinaryPrimitives.WriteUInt64BigEndian(span[(offset + 1)..], htlc.Id);
            BinaryPrimitives.WriteUInt64BigEndian(span[(offset + 9)..], htlc.AmountMsat);
            ((ReadOnlySpan<byte>)htlc.PaymentHash).CopyTo(span[(offset + 17)..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 17 + CryptoConstants.Sha256HashLen)..],
                                                  htlc.CltvExpiry);
        }

        return bytes;
    }

    /// <summary>Decodes <see cref="Encode"/>'s bytes; throws <see cref="FormatException"/> for anything else.</summary>
    public static OutputDescriptorData Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 || bytes[0] != Version)
            throw new FormatException("Not an output descriptor data blob of a known version.");

        var amountSat = BinaryPrimitives.ReadUInt64BigEndian(bytes[1..]);
        var csvDelay = BinaryPrimitives.ReadUInt16BigEndian(bytes[9..]);
        var flags = bytes[11];
        var offset = 12;
        var scriptPubKey = ReadBytes(bytes, ref offset);
        byte[]? witnessScript = null;
        if ((flags & FlagWitnessScript) != 0)
            witnessScript = ReadBytes(bytes, ref offset);

        CompactPubKey? point = null;
        if ((flags & FlagPoint) != 0)
        {
            Require(bytes, offset, CryptoConstants.CompactPubkeyLen);
            point = new CompactPubKey(bytes.Slice(offset, CryptoConstants.CompactPubkeyLen).ToArray());
            offset += CryptoConstants.CompactPubkeyLen;
        }

        SpecHtlc? htlc = null;
        if ((flags & FlagHtlc) != 0)
        {
            Require(bytes, offset, HtlcLength);
            var direction = (HtlcDirection)bytes[offset];
            var id = BinaryPrimitives.ReadUInt64BigEndian(bytes[(offset + 1)..]);
            var amountMsat = BinaryPrimitives.ReadUInt64BigEndian(bytes[(offset + 9)..]);
            var hash = new Hash(bytes.Slice(offset + 17, CryptoConstants.Sha256HashLen).ToArray());
            var cltv = BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 17 + CryptoConstants.Sha256HashLen)..]);
            htlc = new SpecHtlc(direction, id, amountMsat, hash, cltv);
            offset += HtlcLength;
        }

        if (offset != bytes.Length)
            throw new FormatException("Trailing bytes after the output descriptor data.");

        return new OutputDescriptorData(amountSat, scriptPubKey, witnessScript, csvDelay, (flags & FlagAnchors) != 0,
                                        point, htlc);
    }

    /// <summary>Decodes the data of a row, or null when it has none or it is not in this encoding.</summary>
    public static OutputDescriptorData? TryDecode(OutputResolutionModel output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.DescriptorData.Length == 0)
            return null;

        try
        {
            return Decode(output.DescriptorData);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static int WriteBytes(Span<byte> span, int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], (ushort)value.Length);
        value.CopyTo(span[(offset + 2)..]);
        return offset + 2 + value.Length;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> bytes, ref int offset)
    {
        Require(bytes, offset, 2);
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
        Require(bytes, offset + 2, length);
        var value = bytes.Slice(offset + 2, length).ToArray();
        offset += 2 + length;
        return value;
    }

    private static void Require(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        if (offset + length > bytes.Length)
            throw new FormatException("The output descriptor data is truncated.");
    }
}