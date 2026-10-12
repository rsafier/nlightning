namespace NLightning.Domain.Transport;

/// <summary>
/// Computes the ECDH of a protected local static private key (held by a key manager, e.g. the node key for BOLT 8)
/// with a public key, writing the shared secret. The private key never leaves the key manager (NL-436).
/// </summary>
/// <param name="publicKey">A 33-byte compressed secp256k1 public key.</param>
/// <param name="sharedSecret">The 32-byte destination of the shared secret.</param>
public delegate void ProtectedStaticEcdh(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret);