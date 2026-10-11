// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Node;

/// <summary>
/// One LND gRPC macaroon root key (NL-1389, migration <c>AddSharedDatabaseHardening</c>), encrypted with the node data
/// key. <see cref="RootKeyId"/> holds LND's unsigned 64-bit id bit for bit.
/// </summary>
public class MacaroonRootKeyEntity
{
    public required long RootKeyId { get; set; }

    /// <summary>The 24-byte XChaCha20-Poly1305 nonce.</summary>
    public required byte[] Nonce { get; set; }

    /// <summary>The encrypted 32-byte root key and its 16-byte tag.</summary>
    public required byte[] Ciphertext { get; set; }

    public required long CreatedAt { get; set; }

    internal MacaroonRootKeyEntity() { }
}