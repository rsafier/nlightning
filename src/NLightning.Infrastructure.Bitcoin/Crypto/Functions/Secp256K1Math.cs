using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Crypto.Functions;

using Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;

/// <summary>
/// secp256k1 point/scalar arithmetic backed by NBitcoin.Secp256k1 on the blinded <see cref="NLightningCryptoContext"/>.
/// </summary>
internal sealed class Secp256K1Math : ISecp256K1Math
{
    /// <inheritdoc/>
    public CompactPubKey MultiplyPubKey(CompactPubKey pubKey, ReadOnlySpan<byte> scalar)
    {
        EnsureValidScalar(scalar, nameof(scalar));
        var ecPubKey = CreateEcPubKey(pubKey, nameof(pubKey));

        ECPubKey result;
        try
        {
            result = ecPubKey.TweakMul(scalar);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException("Failed to multiply public key by scalar", e);
        }

        return result.ToBytes(true);
    }

    /// <inheritdoc/>
    public PrivKey MultiplyPrivKey(PrivKey privKey, ReadOnlySpan<byte> scalar)
    {
        EnsureValidScalar(scalar, nameof(scalar));
        using var ecPrivKey = CreateEcPrivKey(privKey, nameof(privKey));

        ECPrivKey result;
        try
        {
            result = ecPrivKey.TweakMul(scalar);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException("Failed to multiply private key by scalar", e);
        }

        using (result)
        {
            return ToPrivKey(result);
        }
    }

    /// <inheritdoc/>
    public CompactPubKey AddPubKeys(CompactPubKey pubKey1, CompactPubKey pubKey2)
    {
        var ecPubKey1 = CreateEcPubKey(pubKey1, nameof(pubKey1));
        var ecPubKey2 = CreateEcPubKey(pubKey2, nameof(pubKey2));

        if (!ECPubKey.TryCombine(NLightningCryptoContext.Instance, [ecPubKey1, ecPubKey2], out var combinedPubKey)
         || combinedPubKey is null)
            throw new InvalidOperationException("Failed to combine public keys");

        return combinedPubKey.ToBytes(true);
    }

    /// <inheritdoc/>
    public PrivKey AddPrivKeys(PrivKey privKey1, PrivKey privKey2)
    {
        EnsureValidScalar(privKey2.Value, nameof(privKey2));
        using var ecPrivKey1 = CreateEcPrivKey(privKey1, nameof(privKey1));

        ECPrivKey result;
        try
        {
            result = ecPrivKey1.TweakAdd(privKey2.Value);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException("Failed to add private keys", e);
        }

        using (result)
        {
            return ToPrivKey(result);
        }
    }

    /// <summary>
    /// Ensures the scalar is exactly 32 bytes and lies in [1, n-1].
    /// </summary>
    private static void EnsureValidScalar(ReadOnlySpan<byte> scalar, string paramName)
    {
        if (scalar.Length != CryptoConstants.PrivkeyLen)
            throw new ArgumentException($"Scalar must be {CryptoConstants.PrivkeyLen} bytes", paramName);

        var s = new Scalar(scalar, out var overflow);
        if (overflow != 0 || s.IsZero)
            throw new ArgumentException("Scalar must be in the range [1, n-1]", paramName);
    }

    private static ECPubKey CreateEcPubKey(CompactPubKey pubKey, string paramName)
    {
        ReadOnlySpan<byte> bytes = pubKey;
        if (bytes.Length != CryptoConstants.CompactPubkeyLen
         || !ECPubKey.TryCreate(bytes, NLightningCryptoContext.Instance, out _, out var ecPubKey)
         || ecPubKey is null)
            throw new ArgumentException("Invalid public key", paramName);

        return ecPubKey;
    }

    private static ECPrivKey CreateEcPrivKey(PrivKey privKey, string paramName)
    {
        EnsureValidScalar(privKey.Value, paramName);
        if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(privKey.Value, out var ecPrivKey) || ecPrivKey is null)
            throw new ArgumentException("Invalid private key", paramName);

        return ecPrivKey;
    }

    private static PrivKey ToPrivKey(ECPrivKey ecPrivKey)
    {
        var bytes = new byte[CryptoConstants.PrivkeyLen];
        ecPrivKey.WriteToSpan(bytes);
        return bytes;
    }
}