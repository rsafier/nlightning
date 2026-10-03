using System.Buffers.Binary;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The blob formats of <c>CommitmentEntity.Htlcs</c> and <c>CommitmentEntity.HtlcSignatures</c>.
/// </summary>
internal static class CommitmentStateEncoding
{
    /// <summary>Direction (1) + id (8) + amount msat (8) + payment hash (32) + cltv expiry (4).</summary>
    public const int SpecHtlcLength = 1 + 8 + 8 + CryptoConstants.Sha256HashLen + 4;

    public static byte[] EncodeSpecHtlcs(IReadOnlyList<SpecHtlc> htlcs)
    {
        var bytes = new byte[htlcs.Count * SpecHtlcLength];
        for (var i = 0; i < htlcs.Count; i++)
        {
            var htlc = htlcs[i];
            var span = bytes.AsSpan(i * SpecHtlcLength, SpecHtlcLength);
            span[0] = (byte)htlc.Direction;
            BinaryPrimitives.WriteUInt64BigEndian(span[1..], htlc.Id);
            BinaryPrimitives.WriteUInt64BigEndian(span[9..], htlc.AmountMsat);
            ((ReadOnlySpan<byte>)htlc.PaymentHash).CopyTo(span[17..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(17 + CryptoConstants.Sha256HashLen)..], htlc.CltvExpiry);
        }

        return bytes;
    }

    public static List<SpecHtlc> DecodeSpecHtlcs(byte[] bytes)
    {
        if (bytes.Length % SpecHtlcLength != 0)
            throw new InvalidOperationException(
                $"Commitment HTLC blob has {bytes.Length} bytes, not a multiple of {SpecHtlcLength}");

        var htlcs = new List<SpecHtlc>(bytes.Length / SpecHtlcLength);
        for (var offset = 0; offset < bytes.Length; offset += SpecHtlcLength)
        {
            var span = bytes.AsSpan(offset, SpecHtlcLength);
            var direction = (HtlcDirection)span[0];
            if (!Enum.IsDefined(direction))
                throw new InvalidOperationException($"Commitment HTLC blob has an unknown direction {span[0]}");

            htlcs.Add(new SpecHtlc(direction, BinaryPrimitives.ReadUInt64BigEndian(span[1..]),
                                   BinaryPrimitives.ReadUInt64BigEndian(span[9..]),
                                   new Hash(span.Slice(17, CryptoConstants.Sha256HashLen).ToArray()),
                                   BinaryPrimitives.ReadUInt32BigEndian(span[(17 + CryptoConstants.Sha256HashLen)..])));
        }

        return htlcs;
    }

    /// <summary>Each signature as a length byte followed by the signature bytes, in order.</summary>
    public static byte[] EncodeSignatures(IReadOnlyList<CompactSignature> signatures)
    {
        using var stream = new MemoryStream();
        foreach (var signature in signatures)
        {
            stream.WriteByte(checked((byte)signature.Value.Length));
            stream.Write(signature.Value);
        }

        return stream.ToArray();
    }

    public static List<CompactSignature> DecodeSignatures(byte[] bytes)
    {
        var signatures = new List<CompactSignature>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var length = bytes[offset++];
            if (offset + length > bytes.Length)
                throw new InvalidOperationException("Truncated HTLC signature blob");

            signatures.Add(new CompactSignature(bytes.AsSpan(offset, length).ToArray()));
            offset += length;
        }

        return signatures;
    }

    /// <summary>Funding txid (32) + local balance delta msat (8) + remote balance delta msat (8), big-endian.</summary>
    public const int SignedOnFundingLength = CryptoConstants.Sha256HashLen + 8 + 8;

    /// <summary>
    /// The blob of <c>CommitmentEntity.SignedOnFundings</c> (NL-494): per funding a remote commitment was signed on, its
    /// txid and the engine's balance deltas against the current funding, in order; null for null.
    /// </summary>
    /// <remarks>
    /// The deltas are stored because the <c>ChannelFundings</c> rows' deltas are against the funding that was current
    /// when each row was written (a lock zeroes the new current's and keeps the replaced one's at 0), while the engine
    /// rebases every entry on each lock.
    /// </remarks>
    public static byte[]? EncodeSignedOnFundings(IReadOnlyList<ChannelFunding>? fundings)
    {
        if (fundings is null)
            return null;

        var bytes = new byte[fundings.Count * SignedOnFundingLength];
        for (var i = 0; i < fundings.Count; i++)
        {
            var funding = fundings[i];
            var span = bytes.AsSpan(i * SignedOnFundingLength, SignedOnFundingLength);
            ((ReadOnlySpan<byte>)funding.FundingTxId).CopyTo(span);
            BinaryPrimitives.WriteInt64BigEndian(span[CryptoConstants.Sha256HashLen..], funding.LocalBalanceDeltaMsat);
            BinaryPrimitives.WriteInt64BigEndian(span[(CryptoConstants.Sha256HashLen + 8)..],
                                                 funding.RemoteBalanceDeltaMsat);
        }

        return bytes;
    }

    public static List<SignedOnFundingEntry> DecodeSignedOnFundings(byte[] bytes)
    {
        if (bytes.Length % SignedOnFundingLength != 0)
            throw new InvalidOperationException(
                $"Signed-on fundings blob has {bytes.Length} bytes, not a multiple of {SignedOnFundingLength}");

        var entries = new List<SignedOnFundingEntry>(bytes.Length / SignedOnFundingLength);
        for (var offset = 0; offset < bytes.Length; offset += SignedOnFundingLength)
        {
            var span = bytes.AsSpan(offset, SignedOnFundingLength);
            entries.Add(new SignedOnFundingEntry(new TxId(span[..CryptoConstants.Sha256HashLen].ToArray()),
                                                 BinaryPrimitives.ReadInt64BigEndian(
                                                     span[CryptoConstants.Sha256HashLen..]),
                                                 BinaryPrimitives.ReadInt64BigEndian(
                                                     span[(CryptoConstants.Sha256HashLen + 8)..])));
        }

        return entries;
    }

    /// <summary>One stored entry of <c>RemoteCommit.SignedOnFundings</c>.</summary>
    internal readonly record struct SignedOnFundingEntry(TxId FundingTxId, long LocalBalanceDeltaMsat,
                                                         long RemoteBalanceDeltaMsat);
}