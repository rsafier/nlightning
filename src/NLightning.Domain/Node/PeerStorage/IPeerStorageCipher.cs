namespace NLightning.Domain.Node.PeerStorage;

/// <summary>
/// Authenticated encryption of our own peer storage blobs with a key only this node can derive (from its node key),
/// so a peer can neither read nor alter what it keeps for us (BOLT 1: "MUST encrypt the data in a manner that ensures
/// its integrity upon receipt").
/// </summary>
public interface IPeerStorageCipher
{
    /// <summary>
    /// The plaintext length that encrypts to exactly <see cref="PeerStorageConstants.MaxBlobLength"/> bytes.
    /// </summary>
    int MaxPlaintextLength { get; }

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> (at most <see cref="MaxPlaintextLength"/> bytes) with a fresh nonce.
    /// </summary>
    /// <exception cref="ArgumentException">The plaintext is too long.</exception>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Decrypts a blob made by <see cref="Encrypt"/>, or returns null when it was not made with our key or was
    /// altered.
    /// </summary>
    byte[]? TryDecrypt(ReadOnlySpan<byte> blob);
}