namespace NLightning.Domain.Crypto.Hashes;

using Constants;

/// <summary>
/// One-shot hashing over an <see cref="ISha256"/> that never leaves data behind in its state.
/// </summary>
/// <remarks>
/// The registered <see cref="ISha256"/> keeps one state per thread for the life of the thread (NL-247): a caller that
/// failed between <see cref="ISha256.AppendData"/> and <see cref="ISha256.GetHashAndReset"/> would hand its bytes to
/// the next caller on that thread. These helpers finalize (and so reset) the state even when appending throws.
/// </remarks>
public static class Sha256Extensions
{
    /// <summary>Writes SHA256(<paramref name="data"/>) into <paramref name="hash"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="hash"/> is not 32 bytes long.</exception>
    public static void ComputeHash(this ISha256 sha256, ReadOnlySpan<byte> data, Span<byte> hash)
    {
        ComputeHash(sha256, data, ReadOnlySpan<byte>.Empty, hash);
    }

    /// <summary>Writes SHA256(<paramref name="first"/> || <paramref name="second"/>) into <paramref name="hash"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="hash"/> is not 32 bytes long.</exception>
    public static void ComputeHash(this ISha256 sha256, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second,
                                   Span<byte> hash)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        if (hash.Length != CryptoConstants.Sha256HashLen)
            throw new ArgumentException($"The hash buffer must be {CryptoConstants.Sha256HashLen} bytes long.",
                                        nameof(hash));

        try
        {
            sha256.AppendData(first);
            if (!second.IsEmpty)
                sha256.AppendData(second);
        }
        finally
        {
            // On success this is the result; on failure it only resets the state, and the exception propagates
            sha256.GetHashAndReset(hash);
        }
    }
}