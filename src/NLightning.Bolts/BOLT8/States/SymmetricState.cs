using System.Diagnostics;

namespace NLightning.Bolts.BOLT8.States;

using Ciphers;
using Constants;
using Hashes;
using Primitives;

/// <summary>
/// A SymmetricState object contains a CipherState plus ck (a chaining
/// key of HashLen bytes) and h (a hash output of HashLen bytes).
/// </summary>
internal sealed class SymmetricState : IDisposable
{
    private readonly Sha256 _hash = new();
    private readonly Hkdf _hkdf = new();
    private readonly CipherState _state = new();
    private readonly byte[] _ck;
    private readonly byte[] _h;
    private bool _disposed;

    /// <summary>
    /// Initializes a new SymmetricState with an
    /// arbitrary-length protocolName byte sequence.
    /// </summary>
    public SymmetricState(ReadOnlySpan<byte> protocolName)
    {
        _ck = new byte[HashConstants.HASH_LEN];
        _h = new byte[HashConstants.HASH_LEN];

        if (protocolName.Length <= HashConstants.HASH_LEN)
        {
            protocolName.CopyTo(_h);
        }
        else
        {
            _hash.AppendData(protocolName);
            _hash.GetHashAndReset(_h);
        }

        Array.Copy(_h, _ck, HashConstants.HASH_LEN);
    }

    /// <summary>
    /// Sets ck, tempK = HKDF(ck, inputKeyMaterial, 2).
    /// If HashLen is 64, then truncates tempK to 32 bytes.
    /// Calls InitializeKey(tempK).
    /// </summary>
    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var length = inputKeyMaterial.Length;
        Debug.Assert(length == 0 || length == ChaCha20Poly1305.KEY_SIZE);

        Span<byte> output = stackalloc byte[2 * HashConstants.HASH_LEN];
        _hkdf.ExtractAndExpand2(_ck, inputKeyMaterial, output);

        output[..HashConstants.HASH_LEN].CopyTo(_ck);

        var tempK = output.Slice(HashConstants.HASH_LEN, ChaCha20Poly1305.KEY_SIZE);
        _state.InitializeKey(tempK);
    }

    /// <summary>
    /// Sets h = HASH(h || data).
    /// </summary>
    public void MixHash(ReadOnlySpan<byte> data)
    {
        _hash.AppendData(_h);
        _hash.AppendData(data);
        _hash.GetHashAndReset(_h);
    }

    /// <summary>
    /// Sets ck, tempH, tempK = HKDF(ck, inputKeyMaterial, 3).
    /// Calls MixHash(tempH).
    /// If HashLen is 64, then truncates tempK to 32 bytes.
    /// Calls InitializeKey(tempK).
    /// </summary>
    public void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var length = inputKeyMaterial.Length;
        Debug.Assert(length is 0 or ChaCha20Poly1305.KEY_SIZE);

        Span<byte> output = stackalloc byte[3 * HashConstants.HASH_LEN];
        _hkdf.ExtractAndExpand3(_ck, inputKeyMaterial, output);

        output[..HashConstants.HASH_LEN].CopyTo(_ck);

        var tempH = output.Slice(HashConstants.HASH_LEN, HashConstants.HASH_LEN);
        var tempK = output.Slice(2 * HashConstants.HASH_LEN, ChaCha20Poly1305.KEY_SIZE);

        MixHash(tempH);
        _state.InitializeKey(tempK);
    }

    /// <summary>
    /// Returns h. This function should only be called at the end of
    /// a handshake, i.e. after the Split() function has been called.
    /// </summary>
    public byte[] GetHandshakeHash()
    {
        return _h;
    }

    /// <summary>
    /// Sets ciphertext = EncryptWithAd(h, plaintext),
    /// calls MixHash(ciphertext), and returns ciphertext.
    /// </summary>
    public int EncryptAndHash(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext)
    {
        var bytesWritten = _state.EncryptWithAd(_h, plaintext, ciphertext);
        MixHash(ciphertext[..bytesWritten]);

        return bytesWritten;
    }

    /// <summary>
    /// Sets plaintext = DecryptWithAd(h, ciphertext),
    /// calls MixHash(ciphertext), and returns plaintext.
    /// </summary>
    public int DecryptAndHash(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        var bytesRead = _state.DecryptWithAd(_h, ciphertext, plaintext);
        MixHash(ciphertext);

        return bytesRead;
    }

    /// <summary>
    /// Returns a pair of CipherState objects for encrypting transport messages.
    /// </summary>
    public (CipherState c1, CipherState c2) Split()
    {
        Span<byte> output = stackalloc byte[2 * HashConstants.HASH_LEN];
        _hkdf.ExtractAndExpand2(_ck, null, output);

        var tempK1 = output[..ChaCha20Poly1305.KEY_SIZE];
        var tempK2 = output.Slice(HashConstants.HASH_LEN, ChaCha20Poly1305.KEY_SIZE);

        var c1 = new CipherState();
        var c2 = new CipherState();

        c1.InitializeKeyAndChainingKey(tempK1, _ck);
        c2.InitializeKeyAndChainingKey(tempK2, _ck);

        return (c1, c2);
    }

    /// <summary>
    /// Returns true if k is non-empty, false otherwise.
    /// </summary>
    public bool HasKey()
    {
        return _state.HasKey();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _hash.Dispose();
            _hkdf.Dispose();
            _state.Dispose();
            Utilities.ZeroMemory(_ck);
            _disposed = true;
        }
    }
}