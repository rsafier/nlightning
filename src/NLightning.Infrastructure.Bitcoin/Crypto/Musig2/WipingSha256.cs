using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Bitcoin.Crypto.Musig2;

/// <summary>
/// A managed SHA-256 (FIPS 180-4) whose whole state lives in pinned arrays that it zeroes deterministically: after
/// <see cref="GetHash"/>, on <see cref="Dispose"/> and so on every exception path of a <c>using</c>. BIP 327 hashes
/// that absorb secrets (<c>rand'</c>, the secret key, <c>sk'</c>: <c>MuSig/aux</c>, <c>MuSig/nonce</c>,
/// <c>MuSig/deterministic/nonce</c>) go through it instead of NBitcoin's tagged SHA-256, whose platform hash object
/// keeps its state in native memory we cannot wipe and hands the digest out through a copy (NL-911). The compression
/// function's message schedule is a stack buffer zeroed after each block; the eight working variables are locals, the
/// .NET residual of SR-09.
/// </summary>
internal sealed class WipingSha256 : IDisposable
{
    private const int BlockLen = 64;

    /// <summary>The digest length in bytes.</summary>
    public const int HashLen = 32;

    private static readonly uint[] s_k =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
    ];

    // Test hook: when set, every instance made in this async flow is recorded, so a test can check that each one was
    // wiped after a BIP 327 call returned or threw
    internal static readonly AsyncLocal<List<WipingSha256>?> Tracker = new();

    private readonly uint[] _state = GC.AllocateArray<uint>(8, pinned: true);
    private readonly byte[] _buffer = GC.AllocateArray<byte>(BlockLen, pinned: true);
    private int _bufferLength;
    private ulong _length;

    public WipingSha256()
    {
        Tracker.Value?.Add(this);
        Initialize();
    }

    /// <summary>True when every byte of the state, the pending block and the counters is zero.</summary>
    internal bool IsWiped =>
        _bufferLength == 0 && _length == 0 && !_state.AsSpan().ContainsAnyExcept(0u)
     && !_buffer.AsSpan().ContainsAnyExcept((byte)0);

    /// <summary>
    /// A hasher started with BIP 340's tag prefix <c>SHA256(tag) || SHA256(tag)</c> (the tag is public).
    /// </summary>
    public static WipingSha256 CreateTagged(string tag)
    {
        Span<byte> tagHash = stackalloc byte[HashLen];
        SHA256.HashData(Encoding.ASCII.GetBytes(tag), tagHash);
        var sha = new WipingSha256();
        sha.Write(tagHash);
        sha.Write(tagHash);
        return sha;
    }

    public void Write(byte value) => Write(new ReadOnlySpan<byte>(in value));

    public void Write(ReadOnlySpan<byte> data)
    {
        _length += (ulong)data.Length;
        if (_bufferLength > 0)
        {
            var take = Math.Min(BlockLen - _bufferLength, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_bufferLength));
            _bufferLength += take;
            data = data[take..];
            if (_bufferLength < BlockLen)
                return;

            Compress(_buffer);
            _bufferLength = 0;
        }

        while (data.Length >= BlockLen)
        {
            Compress(data[..BlockLen]);
            data = data[BlockLen..];
        }

        data.CopyTo(_buffer);
        _bufferLength = data.Length;
    }

    /// <summary>Writes the 32-byte digest into <paramref name="destination"/>, then wipes the hasher.</summary>
    public void GetHash(Span<byte> destination)
    {
        if (destination.Length < HashLen)
            throw new ArgumentException($"The destination must hold {HashLen} bytes.", nameof(destination));

        try
        {
            var bitLength = _length * 8;
            _buffer[_bufferLength++] = 0x80;
            if (_bufferLength > BlockLen - 8)
            {
                _buffer.AsSpan(_bufferLength).Clear();
                Compress(_buffer);
                _bufferLength = 0;
            }

            _buffer.AsSpan(_bufferLength, BlockLen - 8 - _bufferLength).Clear();
            BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(BlockLen - 8), bitLength);
            Compress(_buffer);

            for (var i = 0; i < 8; i++)
                BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(i * 4, 4), _state[i]);
        }
        finally
        {
            Clear();
        }
    }

    /// <summary>Zeroes the state, the pending block and the counters; the hasher must not be used afterwards.</summary>
    public void Clear()
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_state.AsSpan()));
        CryptographicOperations.ZeroMemory(_buffer);
        _bufferLength = 0;
        _length = 0;
    }

    public void Dispose() => Clear();

    private void Initialize()
    {
        _state[0] = 0x6a09e667;
        _state[1] = 0xbb67ae85;
        _state[2] = 0x3c6ef372;
        _state[3] = 0xa54ff53a;
        _state[4] = 0x510e527f;
        _state[5] = 0x9b05688c;
        _state[6] = 0x1f83d9ab;
        _state[7] = 0x5be0cd19;
    }

    private void Compress(ReadOnlySpan<byte> block)
    {
        Span<uint> w = stackalloc uint[64];
        try
        {
            for (var t = 0; t < 16; t++)
                w[t] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(t * 4, 4));
            for (var t = 16; t < 64; t++)
            {
                var s0 = uint.RotateRight(w[t - 15], 7) ^ uint.RotateRight(w[t - 15], 18) ^ (w[t - 15] >> 3);
                var s1 = uint.RotateRight(w[t - 2], 17) ^ uint.RotateRight(w[t - 2], 19) ^ (w[t - 2] >> 10);
                w[t] = w[t - 16] + s0 + w[t - 7] + s1;
            }

            uint a = _state[0], b = _state[1], c = _state[2], d = _state[3];
            uint e = _state[4], f = _state[5], g = _state[6], h = _state[7];
            for (var t = 0; t < 64; t++)
            {
                var sum1 = uint.RotateRight(e, 6) ^ uint.RotateRight(e, 11) ^ uint.RotateRight(e, 25);
                var ch = (e & f) ^ (~e & g);
                var temp1 = h + sum1 + ch + s_k[t] + w[t];
                var sum0 = uint.RotateRight(a, 2) ^ uint.RotateRight(a, 13) ^ uint.RotateRight(a, 22);
                var maj = (a & b) ^ (a & c) ^ (b & c);
                var temp2 = sum0 + maj;
                h = g;
                g = f;
                f = e;
                e = d + temp1;
                d = c;
                c = b;
                b = a;
                a = temp1 + temp2;
            }

            _state[0] += a;
            _state[1] += b;
            _state[2] += c;
            _state[3] += d;
            _state[4] += e;
            _state[5] += f;
            _state[6] += g;
            _state[7] += h;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(w));
        }
    }
}