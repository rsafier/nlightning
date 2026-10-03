namespace NLightning.Infrastructure.Crypto.Interfaces;

internal interface ICryptoProvider : IDisposable
{
    #region Sha256
    void Sha256Init(IntPtr state);

    void Sha256Update(IntPtr state, ReadOnlySpan<byte> data);

    void Sha256Final(IntPtr state, Span<byte> result);
    #endregion

    #region AeadChacha20Poly1305Ietf
    int AeadChaCha20Poly1305IetfEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> publicNonce,
                                         ReadOnlySpan<byte> secureNonce, ReadOnlySpan<byte> authenticationData,
                                         ReadOnlySpan<byte> plainText, Span<byte> cipherText, out long cipherTextLength);
    int AeadChaCha20Poly1305IetfDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> publicNonce,
                                         ReadOnlySpan<byte> secureNonce, ReadOnlySpan<byte> authenticationData,
                                         ReadOnlySpan<byte> cipherText, Span<byte> plainText, out long plainTextLength);
    #endregion

    #region StreamChaCha20Ietf
    /// <summary>
    /// XORs <paramref name="input"/> with the raw ChaCha20 (RFC 8439, IETF variant) keystream generated from a
    /// 32-byte <paramref name="key"/> and a 12-byte <paramref name="nonce"/>, starting at block counter 0, writing the
    /// result into <paramref name="output"/>. This is not an AEAD; no tag is produced.
    /// </summary>
    /// <remarks><paramref name="output"/> must be the same length as <paramref name="input"/>. They may be the exact
    /// same memory (in-place), but must not partially overlap.</remarks>
    /// <returns>0 on success, non-zero on failure.</returns>
    int StreamChaCha20IetfXor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input,
                              Span<byte> output);
    #endregion

    #region Memory Operations
    IntPtr MemoryAlloc(ulong size);
    int MemoryLock(IntPtr addr, ulong len);
    void MemoryFree(IntPtr ptr);
    void MemoryZero(IntPtr ptr, ulong len);
    void MemoryUnlock(IntPtr addr, ulong len);
    #endregion

    #region AeadXChaCha20Poly1305Ietf
    int AeadXChaCha20Poly1305IetfEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
                                         ReadOnlySpan<byte> additionalData, ReadOnlySpan<byte> plainText,
                                         Span<byte> cipherText, out long cipherTextLength);

    int AeadXChaCha20Poly1305IetfDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
                                         ReadOnlySpan<byte> additionalData, ReadOnlySpan<byte> cipherText,
                                         Span<byte> plainText, out long plainTextLength);
    #endregion

    #region Key Derivation From Password
    /// <summary>
    /// Derives <paramref name="key"/> with Argon2id from the raw <paramref name="password"/> bytes (all of them).
    /// </summary>
    int DeriveKeyFromPasswordUsingArgon2I(Span<byte> key, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt,
                                          ulong opsLimit, ulong memLimit);
    #endregion

    #region Random
    void RandomBytes(Span<byte> buffer);
    #endregion
}