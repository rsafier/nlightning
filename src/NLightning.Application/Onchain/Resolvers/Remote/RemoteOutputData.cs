using System.Buffers.Binary;

namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// A confirmed spend of a remote-commitment output as the resolver recorded it (the facts the planner needs again on
/// every block, <see cref="OutputSpend"/>).
/// </summary>
/// <param name="SpendingTxId">The spending transaction.</param>
/// <param name="Height">The height of the block holding it.</param>
/// <param name="ByUs">True when it is one of our sweeps or claims.</param>
/// <param name="Path">The HTLC spend path read from its witness (<see cref="HtlcSpendPath.Unknown"/> for
/// <c>to_remote</c>).</param>
/// <param name="Preimage">The preimage the witness revealed, checked against the payment hash; null when none.</param>
public sealed record RemoteRecordedSpend(TxId SpendingTxId, uint Height, bool ByUs, HtlcSpendPath Path,
                                         byte[]? Preimage)
{
    public OutputSpend ToOutputSpend() => new(SpendingTxId, Height, ByUs, Path, Preimage);
}

/// <summary>
/// What <see cref="RemoteCommitResolver"/> keeps in <see cref="OutputResolutionModel.DescriptorData"/> for one output
/// of a peer commitment on chain (BOLT 5 plan §3.6: "what the descriptor needs beyond the outpoint, never keys"): the
/// output (amount, scripts, CSV, HTLC), the peer's per-commitment point of that commitment (a public point: the claim
/// keys are re-derived from it by the signer), the recorded spend and whether the upstream event was asked for.
/// </summary>
/// <remarks>
/// Encoding (big-endian, version 1): <c>version(1) flags(1) amount_sat(8) csv(2) spk_len(2) spk ws_len(2) ws
/// [htlc: direction(1) id(8) amount_msat(8) payment_hash(32) cltv_expiry(4)] [point(33)] [spend: txid(32) height(4)
/// by_us(1) path(1) has_preimage(1) [preimage(32)]]</c>. Flags: 1 anchors, 2 witness script, 4 HTLC, 8 point,
/// 16 upstream raised, 32 spend.
/// </remarks>
public sealed record RemoteOutputData(
    ulong AmountSat,
    ushort CsvDelay,
    bool HasAnchors,
    byte[] ScriptPubKey,
    byte[]? WitnessScript,
    SpecHtlc? Htlc,
    CompactPubKey? RemotePerCommitmentPoint,
    bool UpstreamRaised = false,
    RemoteRecordedSpend? Spend = null)
{
    private const byte Version = 1;
    private const byte FlagAnchors = 1;
    private const byte FlagWitnessScript = 2;
    private const byte FlagHtlc = 4;
    private const byte FlagPoint = 8;
    private const byte FlagUpstreamRaised = 16;
    private const byte FlagSpend = 32;
    private const int HashLength = 32;
    private const int PointLength = 33;

    /// <summary>The data of a freshly mapped output.</summary>
    public static RemoteOutputData FromDescriptor(CommitmentOutputDescriptor descriptor,
                                                  CompactPubKey? remotePerCommitmentPoint)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new RemoteOutputData(descriptor.AmountSat, descriptor.CsvDelay, descriptor.HasAnchors,
                                    descriptor.ScriptPubKey, descriptor.WitnessScript, descriptor.Htlc,
                                    remotePerCommitmentPoint);
    }

    /// <summary>The descriptor the planner and the sweep input factory work on.</summary>
    public CommitmentOutputDescriptor ToDescriptor(uint vout, OutputDescriptorKind kind) =>
        new(vout, AmountSat, kind, ScriptPubKey, WitnessScript, Htlc, CsvDelay, HasAnchors);

    public byte[] Encode()
    {
        var length = 1 + 1 + 8 + 2 + 2 + ScriptPubKey.Length + 2 + (WitnessScript?.Length ?? 0)
                   + (Htlc is null ? 0 : 1 + 8 + 8 + HashLength + 4)
                   + (RemotePerCommitmentPoint is null ? 0 : PointLength)
                   + (Spend is null ? 0 : HashLength + 4 + 1 + 1 + 1 + (Spend.Preimage is null ? 0 : HashLength));
        var buffer = new byte[length];
        var span = buffer.AsSpan();
        var offset = 0;

        var flags = (byte)((HasAnchors ? FlagAnchors : 0) | (WitnessScript is null ? 0 : FlagWitnessScript)
                         | (Htlc is null ? 0 : FlagHtlc) | (RemotePerCommitmentPoint is null ? 0 : FlagPoint)
                         | (UpstreamRaised ? FlagUpstreamRaised : 0) | (Spend is null ? 0 : FlagSpend));
        span[offset++] = Version;
        span[offset++] = flags;
        BinaryPrimitives.WriteUInt64BigEndian(span[offset..], AmountSat);
        offset += 8;
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], CsvDelay);
        offset += 2;
        offset = WriteBytes(span, offset, ScriptPubKey);
        offset = WriteBytes(span, offset, WitnessScript ?? []);

        if (Htlc is { } htlc)
        {
            span[offset++] = (byte)htlc.Direction;
            BinaryPrimitives.WriteUInt64BigEndian(span[offset..], htlc.Id);
            offset += 8;
            BinaryPrimitives.WriteUInt64BigEndian(span[offset..], htlc.AmountMsat);
            offset += 8;
            ((byte[])htlc.PaymentHash).CopyTo(span[offset..]);
            offset += HashLength;
            BinaryPrimitives.WriteUInt32BigEndian(span[offset..], htlc.CltvExpiry);
            offset += 4;
        }

        if (RemotePerCommitmentPoint is { } point)
        {
            ((byte[])point).CopyTo(span[offset..]);
            offset += PointLength;
        }

        if (Spend is { } spend)
        {
            ((byte[])spend.SpendingTxId).CopyTo(span[offset..]);
            offset += HashLength;
            BinaryPrimitives.WriteUInt32BigEndian(span[offset..], spend.Height);
            offset += 4;
            span[offset++] = (byte)(spend.ByUs ? 1 : 0);
            span[offset++] = (byte)spend.Path;
            span[offset++] = (byte)(spend.Preimage is null ? 0 : 1);
            if (spend.Preimage is { } preimage)
            {
                preimage.CopyTo(span[offset..]);
                offset += HashLength;
            }
        }

        return offset == length ? buffer : throw new InvalidOperationException("Remote output data length mismatch");
    }

    /// <summary>Decodes <see cref="Encode"/>'s bytes.</summary>
    /// <exception cref="FormatException">The bytes are not remote output data of a known version.</exception>
    public static RemoteOutputData Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            var offset = 0;
            if (data[offset++] != Version)
                throw new FormatException($"Unknown remote output data version {data[0]}");

            var flags = data[offset++];
            var amount = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
            offset += 8;
            var csv = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
            var scriptPubKey = ReadBytes(data, ref offset);
            var witnessScript = ReadBytes(data, ref offset);

            SpecHtlc? htlc = null;
            if ((flags & FlagHtlc) != 0)
            {
                var direction = (HtlcDirection)data[offset++];
                var id = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
                offset += 8;
                var amountMsat = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
                offset += 8;
                var hash = new Hash(data.Slice(offset, HashLength).ToArray());
                offset += HashLength;
                var cltv = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
                offset += 4;
                htlc = new SpecHtlc(direction, id, amountMsat, hash, cltv);
            }

            CompactPubKey? point = null;
            if ((flags & FlagPoint) != 0)
            {
                point = new CompactPubKey(data.Slice(offset, PointLength).ToArray());
                offset += PointLength;
            }

            RemoteRecordedSpend? spend = null;
            if ((flags & FlagSpend) != 0)
            {
                var txId = new TxId(data.Slice(offset, HashLength).ToArray());
                offset += HashLength;
                var height = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
                offset += 4;
                var byUs = data[offset++] != 0;
                var path = (HtlcSpendPath)data[offset++];
                byte[]? preimage = null;
                if (data[offset++] != 0)
                {
                    preimage = data.Slice(offset, HashLength).ToArray();
                    offset += HashLength;
                }

                spend = new RemoteRecordedSpend(txId, height, byUs, path, preimage);
            }

            if (offset != data.Length)
                throw new FormatException("Trailing bytes in remote output data");

            return new RemoteOutputData(amount, csv, (flags & FlagAnchors) != 0, scriptPubKey,
                                        (flags & FlagWitnessScript) != 0 ? witnessScript : null, htlc, point,
                                        (flags & FlagUpstreamRaised) != 0, spend);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new FormatException("Truncated remote output data", e);
        }
        catch (IndexOutOfRangeException e)
        {
            throw new FormatException("Truncated remote output data", e);
        }
    }

    private static int WriteBytes(Span<byte> span, int offset, byte[] bytes)
    {
        BinaryPrimitives.WriteUInt16BigEndian(span[offset..], checked((ushort)bytes.Length));
        offset += 2;
        bytes.CopyTo(span[offset..]);
        return offset + bytes.Length;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        var bytes = data.Slice(offset, length).ToArray();
        offset += length;
        return bytes;
    }
}