namespace NLightning.Domain.Node.Credentials;

/// <summary>
/// One LND gRPC macaroon root key as the database keeps it (NL-1389, table <c>MacaroonRootKeys</c>): encrypted with the
/// node data key (<c>NodeDataPurpose.MacaroonRootKey</c>), never in the clear.
/// </summary>
/// <param name="RootKeyId">LND's root key id (0 for the default macaroons).</param>
/// <param name="Nonce">The 24-byte XChaCha20-Poly1305 nonce.</param>
/// <param name="Ciphertext">The encrypted 32-byte root key with its 16-byte tag.</param>
/// <param name="CreatedAt">When the key was stored.</param>
public sealed record StoredMacaroonRootKey(ulong RootKeyId, byte[] Nonce, byte[] Ciphertext, DateTimeOffset CreatedAt);