using System.Buffers.Binary;

namespace NLightning.Domain.Crypto.Hashes;

/// <summary>
/// SHA3-256 (FIPS 202, Keccak-f[1600] with rate 136 and the SHA-3 domain padding <c>0x06</c>). Tor derives the
/// checksum of a v3 onion address with it. The BCL's <c>SHA3_256</c> needs OpenSSL 1.1.1+ and is missing on macOS, so
/// the Domain carries its own (short inputs only: onion checksums are 48 bytes).
/// </summary>
internal static class Sha3
{
    private const int Rate256 = 136;

    private static readonly ulong[] s_roundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL, 0x8000000080008000UL,
        0x000000000000808bUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008aUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800aUL, 0x800000008000000aUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    ];

    private static readonly int[] s_rotations =
    [
        1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44
    ];

    private static readonly int[] s_piLanes =
    [
        10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1
    ];

    /// <summary>
    /// The SHA3-256 digest (32 bytes) of <paramref name="data"/>.
    /// </summary>
    public static byte[] Hash256(ReadOnlySpan<byte> data)
    {
        Span<ulong> state = stackalloc ulong[25];
        Span<byte> block = stackalloc byte[Rate256];

        while (data.Length >= Rate256)
        {
            Absorb(state, data[..Rate256]);
            data = data[Rate256..];
        }

        // Last block: the rest, the SHA-3 domain bits 01 and the pad10*1 start (0x06), the final bit (0x80)
        block.Clear();
        data.CopyTo(block);
        block[data.Length] ^= 0x06;
        block[Rate256 - 1] ^= 0x80;
        Absorb(state, block);

        var digest = new byte[32];
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(digest.AsSpan(i * 8), state[i]);

        return digest;
    }

    private static void Absorb(Span<ulong> state, ReadOnlySpan<byte> block)
    {
        for (var i = 0; i < Rate256 / 8; i++)
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(i * 8)..]);

        KeccakF1600(state);
    }

    private static void KeccakF1600(Span<ulong> a)
    {
        Span<ulong> c = stackalloc ulong[5];
        for (var round = 0; round < 24; round++)
        {
            // Theta
            for (var x = 0; x < 5; x++)
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];

            for (var x = 0; x < 5; x++)
            {
                var d = c[(x + 4) % 5] ^ ulong.RotateLeft(c[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5)
                    a[y + x] ^= d;
            }

            // Rho and pi
            var current = a[1];
            for (var i = 0; i < 24; i++)
            {
                var lane = s_piLanes[i];
                var next = a[lane];
                a[lane] = ulong.RotateLeft(current, s_rotations[i]);
                current = next;
            }

            // Chi
            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++)
                    c[x] = a[y + x];

                for (var x = 0; x < 5; x++)
                    a[y + x] = c[x] ^ (~c[(x + 1) % 5] & c[(x + 2) % 5]);
            }

            // Iota
            a[0] ^= s_roundConstants[round];
        }
    }
}