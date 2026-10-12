using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Codecs;

using Models;
using OnionMessages;

/// <summary>
/// The <c>payment_blinded_path</c> wire codec of BOLTs PR 836 (<c>blinded_path path || blinded_payinfo
/// payment_info</c>) and its <c>...*payment_blinded_path</c> list, the value of the trampoline payload's
/// <c>recipient_blinded_paths</c> (type 22).
/// </summary>
/// <remarks>
/// Built on <see cref="BlindedPathCodec"/> and <see cref="BlindedPayInfoCodec"/>, so the rules are theirs: a path with
/// no hop, an <c>enclen</c> or <c>flen</c> past the end, a point without a 02/03 prefix or an undefined
/// <c>sciddir_or_pubkey</c> prefix is refused. Points are not checked against the curve (the Domain has no secp256k1):
/// an off-curve key fails when it is used.
/// </remarks>
public static class PaymentBlindedPathCodec
{
    /// <summary>
    /// The encoded length of <paramref name="path"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The path or its payinfo cannot be encoded.</exception>
    public static int GetLength(WireBlindedPaymentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return BlindedPathCodec.GetLength(path.Path) + BlindedPayInfoCodec.GetLength(path.PayInfo);
    }

    /// <summary>
    /// Writes <paramref name="path"/> at the start of <paramref name="destination"/>.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException">The path cannot be encoded, or <paramref name="destination"/> is too
    /// short.</exception>
    public static int Write(WireBlindedPaymentPath path, Span<byte> destination)
    {
        var length = GetLength(path);
        if (destination.Length < length)
            throw new ArgumentException($"Destination needs {length} bytes, got {destination.Length}.",
                                        nameof(destination));

        var offset = BlindedPathCodec.Write(path.Path, destination);
        return offset + BlindedPayInfoCodec.Write(path.PayInfo, destination[offset..]);
    }

    /// <summary>
    /// Encodes <paramref name="paths"/> back to back (the <c>recipient_blinded_paths</c> value).
    /// </summary>
    /// <exception cref="ArgumentException">A path cannot be encoded.</exception>
    public static byte[] EncodeList(IReadOnlyList<WireBlindedPaymentPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var bytes = new byte[paths.Sum(GetLength)];
        var offset = 0;
        foreach (var path in paths)
            offset += Write(path, bytes.AsSpan(offset));

        return bytes;
    }

    /// <summary>
    /// Reads one <c>payment_blinded_path</c> from the start of <paramref name="data"/> (trailing bytes are left for the
    /// caller).
    /// </summary>
    /// <param name="data">The bytes to read from.</param>
    /// <param name="path">The decoded path.</param>
    /// <param name="bytesRead">How many bytes it took, 0 on failure.</param>
    /// <param name="reason">Why the bytes are refused.</param>
    public static bool TryRead(ReadOnlySpan<byte> data, [NotNullWhen(true)] out WireBlindedPaymentPath? path,
                               out int bytesRead, [NotNullWhen(false)] out string? reason)
    {
        path = null;
        bytesRead = 0;

        if (!BlindedPathCodec.TryRead(data, out var blindedPath, out var pathLength, out reason))
            return false;

        if (!BlindedPayInfoCodec.TryRead(data[pathLength..], out var payInfo, out var payInfoLength,
                                         out var payInfoReason))
        {
            reason = $"blinded_payinfo {payInfoReason}.";
            return false;
        }

        path = new WireBlindedPaymentPath(blindedPath, payInfo);
        bytesRead = pathLength + payInfoLength;
        return true;
    }

    /// <summary>
    /// Decodes a <c>...*payment_blinded_path</c> value: paths back to back until the end of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">The bytes (empty means no path).</param>
    /// <param name="paths">The decoded paths.</param>
    /// <param name="reason">Why the bytes are refused (the first bad path's reason, with its index).</param>
    public static bool TryReadList(ReadOnlySpan<byte> data,
                                   [NotNullWhen(true)] out IReadOnlyList<WireBlindedPaymentPath>? paths,
                                   [NotNullWhen(false)] out string? reason)
    {
        paths = null;
        var list = new List<WireBlindedPaymentPath>();
        var offset = 0;

        while (offset < data.Length)
        {
            if (!TryRead(data[offset..], out var path, out var bytesRead, out var pathReason))
            {
                reason = $"payment_blinded_path {list.Count}: {pathReason}";
                return false;
            }

            list.Add(path);
            offset += bytesRead;
        }

        paths = list;
        reason = null;
        return true;
    }

    /// <summary>
    /// Decodes a <c>...*payment_blinded_path</c> value.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a list of valid paths.</exception>
    public static IReadOnlyList<WireBlindedPaymentPath> DecodeList(ReadOnlySpan<byte> data)
    {
        return TryReadList(data, out var paths, out var reason) ? paths : throw new FormatException(reason);
    }
}