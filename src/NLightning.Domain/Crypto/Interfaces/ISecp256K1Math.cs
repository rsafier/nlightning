namespace NLightning.Domain.Crypto.Interfaces;

using ValueObjects;

/// <summary>
/// Elliptic-curve arithmetic over secp256k1 (point/scalar tweaks and additions).
/// </summary>
/// <remarks>
/// All scalars are 32-byte big-endian values and must be in the range [1, n-1], where n is the curve order.
/// Implementations throw <see cref="ArgumentException"/> for an invalid scalar or point, and
/// <see cref="InvalidOperationException"/> when the result would be the point at infinity or the zero scalar.
/// </remarks>
public interface ISecp256K1Math
{
    /// <summary>
    /// Computes <c>scalar * pubKey</c> (EC point multiplication).
    /// </summary>
    CompactPubKey MultiplyPubKey(CompactPubKey pubKey, ReadOnlySpan<byte> scalar);

    /// <summary>
    /// Computes <c>scalar * pubKey</c> (EC point multiplication) for a 33-byte compressed public key, without
    /// copying it into a <see cref="CompactPubKey"/> first (NL-083).
    /// </summary>
    CompactPubKey MultiplyPubKey(ReadOnlySpan<byte> pubKey, ReadOnlySpan<byte> scalar);

    /// <summary>
    /// Computes <c>privKey * scalar mod n</c>.
    /// </summary>
    PrivKey MultiplyPrivKey(PrivKey privKey, ReadOnlySpan<byte> scalar);

    /// <summary>
    /// Computes <c>pubKey1 + pubKey2</c> (EC point addition).
    /// </summary>
    CompactPubKey AddPubKeys(CompactPubKey pubKey1, CompactPubKey pubKey2);

    /// <summary>
    /// Computes <c>privKey1 + privKey2 mod n</c>.
    /// </summary>
    PrivKey AddPrivKeys(PrivKey privKey1, PrivKey privKey2);
}