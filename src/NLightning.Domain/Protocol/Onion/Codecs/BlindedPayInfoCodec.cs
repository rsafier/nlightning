using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Codecs;

using Models;

/// <summary>
/// The BOLT 12 <c>blinded_payinfo</c> wire codec: <c>u32 fee_base_msat || u32 fee_proportional_millionths ||
/// u16 cltv_expiry_delta || u64 htlc_minimum_msat || u64 htlc_maximum_msat || u16 flen || flen*byte features</c>.
/// </summary>
/// <remarks>
/// One element; BOLT 12's <c>invoice_blindedpay</c> list (<c>Offers.Encoding.Bolt12FieldCodec</c>) and the trampoline
/// <c>payment_blinded_path</c> (<see cref="PaymentBlindedPathCodec"/>) are built on it.
/// </remarks>
public static class BlindedPayInfoCodec
{
    /// <summary>
    /// The encoded length of one <c>blinded_payinfo</c> without its features.
    /// </summary>
    public const int FixedLength = 4 + 4 + 2 + 8 + 8 + 2;

    /// <summary>
    /// The encoded length of <paramref name="payInfo"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Its features are longer than 65535 bytes.</exception>
    public static int GetLength(BlindedPayInfo payInfo)
    {
        Validate(payInfo);
        return FixedLength + payInfo.Features.Length;
    }

    /// <summary>
    /// Writes <paramref name="payInfo"/> at the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">Its features are longer than 65535 bytes, or
    /// <paramref name="destination"/> is too short.</exception>
    public static int Write(BlindedPayInfo payInfo, Span<byte> destination)
    {
        var length = GetLength(payInfo);
        if (destination.Length < length)
            throw new ArgumentException($"Destination needs {length} bytes, got {destination.Length}.",
                                        nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination, payInfo.FeeBaseMsat);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], payInfo.FeeProportionalMillionths);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], payInfo.CltvExpiryDelta);
        BinaryPrimitives.WriteUInt64BigEndian(destination[10..], payInfo.HtlcMinimumMsat);
        BinaryPrimitives.WriteUInt64BigEndian(destination[18..], payInfo.HtlcMaximumMsat);
        BinaryPrimitives.WriteUInt16BigEndian(destination[26..], (ushort)payInfo.Features.Length);
        payInfo.Features.Span.CopyTo(destination[FixedLength..]);
        return length;
    }

    /// <summary>
    /// Encodes <paramref name="payInfo"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Its features are longer than 65535 bytes.</exception>
    public static byte[] Encode(BlindedPayInfo payInfo)
    {
        var bytes = new byte[GetLength(payInfo)];
        Write(payInfo, bytes);
        return bytes;
    }

    /// <summary>
    /// Reads one <c>blinded_payinfo</c> from the start of <paramref name="data"/> (trailing bytes are left for the
    /// caller).
    /// </summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="payInfo">The decoded payinfo.</param>
    /// <param name="bytesRead">How many bytes it took, 0 on failure.</param>
    /// <param name="reason">Why the bytes are refused (<c>is truncated</c> or <c>features run past the end</c>).</param>
    public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out BlindedPayInfo? payInfo,
                               out int bytesRead, [NotNullWhen(false)] out string? reason)
    {
        payInfo = null;
        bytesRead = 0;

        if (data.Length < FixedLength)
        {
            reason = "is truncated";
            return false;
        }

        var featuresLength = BinaryPrimitives.ReadUInt16BigEndian(data[26..]);
        if (data.Length - FixedLength < featuresLength)
        {
            reason = "features run past the end";
            return false;
        }

        payInfo = new BlindedPayInfo(BinaryPrimitives.ReadUInt32BigEndian(data),
                                     BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
                                     BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
                                     BinaryPrimitives.ReadUInt64BigEndian(data[10..]),
                                     BinaryPrimitives.ReadUInt64BigEndian(data[18..]),
                                     data.Slice(FixedLength, featuresLength).ToArray());
        bytesRead = FixedLength + featuresLength;
        reason = null;
        return true;
    }

    private static void Validate(BlindedPayInfo payInfo)
    {
        ArgumentNullException.ThrowIfNull(payInfo);
        if (payInfo.Features.Length > ushort.MaxValue)
            throw new ArgumentException("A blinded_payinfo's features must fit in a u16 length.", nameof(payInfo));
    }
}