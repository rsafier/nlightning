namespace NLightning.Infrastructure.Bitcoin.Services;

using Crypto.Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;

/// <summary>
/// Verifies revealed per-commitment secrets with secp256k1: <c>per_commitment_secret * G == per_commitment_point</c>.
/// </summary>
public sealed class PerCommitmentSecretVerifier : IPerCommitmentSecretVerifier
{
    /// <inheritdoc />
    public bool Verify(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint)
    {
        byte[] secretBytes = perCommitmentSecret;
        if (secretBytes is not { Length: CryptoConstants.SecretLen })
            return false;

        // Zero or >= n is not a valid scalar, so it cannot be anyone's per-commitment secret
        if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(secretBytes, out var privKey) || privKey is null)
            return false;

        using (privKey)
        {
            var point = privKey.CreatePubKey().ToBytes(true);
            return point.AsSpan().SequenceEqual((ReadOnlySpan<byte>)expectedPerCommitmentPoint);
        }
    }

    /// <inheritdoc />
    public bool VerifyAndStore(Secret perCommitmentSecret, ulong remoteCommitmentNumber,
                               CompactPubKey expectedPerCommitmentPoint, ISecretStorageService remoteShachain)
    {
        ArgumentNullException.ThrowIfNull(remoteShachain);

        // Check the point first: a secret that does not open the commitment never reaches the store
        if (!Verify(perCommitmentSecret, expectedPerCommitmentPoint))
            return false;

        return remoteShachain.InsertSecret(perCommitmentSecret, PerCommitmentIndex.From(remoteCommitmentNumber));
    }
}