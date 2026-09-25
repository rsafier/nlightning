using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Services;

using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Infrastructure.Crypto.Hashes;

public class KeyDerivationService : IKeyDerivationService
{
    private readonly ISecp256K1Math _secp256K1Math;

    public KeyDerivationService(ISecp256K1Math secp256K1Math)
    {
        _secp256K1Math = secp256K1Math;
    }

    /// <summary>
    /// Derives a public key using the formula: basepoint + SHA256(per_commitment_point || basepoint) * G
    /// </summary>
    public CompactPubKey DerivePublicKey(CompactPubKey compactBasepoint, CompactPubKey compactPerCommitmentPoint)
    {
        // Calculate SHA256(per_commitment_point || basepoint)
        Span<byte> hashBytes = stackalloc byte[CryptoConstants.Sha256HashLen];
        ComputeSha256(compactPerCommitmentPoint, compactBasepoint, hashBytes);

        // Get the EC point representation of hash*G
        using var hashPrivateKey = new Key(hashBytes.ToArray());
        CompactPubKey hashPoint = hashPrivateKey.PubKey.ToBytes();

        // basepoint + hash*G
        return _secp256K1Math.AddPubKeys(compactBasepoint, hashPoint);
    }

    /// <summary>
    /// Derives a private key using the formula: basepoint_secret + SHA256(per_commitment_point || basepoint)
    /// </summary>
    public PrivKey DerivePrivateKey(PrivKey basepointSecretPriv, CompactPubKey compactPerCommitmentPoint)
    {
        using var basepointSecret = new Key(basepointSecretPriv);
        CompactPubKey basepoint = basepointSecret.PubKey.ToBytes();

        var hashBytes = new byte[CryptoConstants.Sha256HashLen];
        ComputeSha256(compactPerCommitmentPoint, basepoint, hashBytes);

        return _secp256K1Math.AddPrivKeys(basepointSecretPriv, hashBytes);
    }

    /// <summary>
    /// Derives the revocation public key
    /// </summary>
    public CompactPubKey DeriveRevocationPubKey(CompactPubKey compactRevocationBasepoint,
                                                CompactPubKey compactPerCommitmentPoint)
    {
        Span<byte> hash1 = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> hash2 = stackalloc byte[CryptoConstants.Sha256HashLen];
        ComputeSha256(compactRevocationBasepoint, compactPerCommitmentPoint, hash1);
        ComputeSha256(compactPerCommitmentPoint, compactRevocationBasepoint, hash2);

        // Calculate revocation_basepoint * SHA256(revocation_basepoint || per_commitment_point)
        var term1 = _secp256K1Math.MultiplyPubKey(compactRevocationBasepoint, hash1);

        // Calculate per_commitment_point * SHA256(per_commitment_point || revocation_basepoint)
        var term2 = _secp256K1Math.MultiplyPubKey(compactPerCommitmentPoint, hash2);

        // Add the two terms
        return _secp256K1Math.AddPubKeys(term1, term2);
    }

    /// <summary>
    /// Derives the revocation private key when both secrets are known
    /// </summary>
    public PrivKey DeriveRevocationPrivKey(PrivKey revocationBasepointSecretPriv, PrivKey perCommitmentSecretPriv)
    {
        using var revocationBasepointSecret = new Key(revocationBasepointSecretPriv);
        using var perCommitmentSecret = new Key(perCommitmentSecretPriv);

        CompactPubKey revocationBasepoint = revocationBasepointSecret.PubKey.ToBytes();
        CompactPubKey perCommitmentPoint = perCommitmentSecret.PubKey.ToBytes();

        Span<byte> hash1 = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> hash2 = stackalloc byte[CryptoConstants.Sha256HashLen];
        ComputeSha256(revocationBasepoint, perCommitmentPoint, hash1);
        ComputeSha256(perCommitmentPoint, revocationBasepoint, hash2);

        // Calculate revocation_basepoint_secret * SHA256(revocation_basepoint || per_commitment_point)
        var term1 = _secp256K1Math.MultiplyPrivKey(revocationBasepointSecretPriv, hash1);

        // Calculate per_commitment_secret * SHA256(per_commitment_point || revocation_basepoint)
        var term2 = _secp256K1Math.MultiplyPrivKey(perCommitmentSecretPriv, hash2);

        // Add the two terms
        return _secp256K1Math.AddPrivKeys(term1, term2);
    }

    /// <summary>
    /// Generates per-commitment secret from seed and index
    /// </summary>
    public Secret GeneratePerCommitmentSecret(Secret seed, ulong index)
    {
        using var sha256 = new Sha256();

        var secret = new byte[CryptoConstants.Sha256HashLen];
        Buffer.BlockCopy(seed, 0, secret, 0, CryptoConstants.Sha256HashLen);

        for (var b = 47; b >= 0; b--)
        {
            if (((index >> b) & 1) == 0)
            {
                continue;
            }

            // Flip bit (b % 8) in byte (b / 8)
            secret[b / 8] ^= (byte)(1 << (b % 8));
            sha256.AppendData(secret);
            sha256.GetHashAndReset(secret);
        }

        return secret;
    }

    /// <summary>
    /// Helper method to calculate SHA256(point1 || point2)
    /// </summary>
    private static void ComputeSha256(ReadOnlySpan<byte> point1, ReadOnlySpan<byte> point2, Span<byte> buffer)
    {
        using var sha256 = new Sha256();
        sha256.AppendData(point1);
        sha256.AppendData(point2);
        sha256.GetHashAndReset(buffer);
    }
}