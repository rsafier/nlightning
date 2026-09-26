using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Bitcoin.Wallet.Constants;

using Enums;

/// <summary>
/// The weights the wallet's fee inputs and change add to a transaction (BOLT 5 plan O7-T1). The segwit marker and flag
/// (2 weight units) belong to the rest of the transaction.
/// </summary>
[ExcludeFromCodeCoverage]
public static class WalletWeights
{
    /// <summary>
    /// A P2WPKH input: 41 bytes x 4 plus the witness (item count, a 72-byte low-S signature with its sighash byte, the
    /// 33-byte key, each with its length byte) = 164 + 108.
    /// </summary>
    public const int P2WpkhInputWeight = 272;

    /// <summary>
    /// A P2TR key path input: 41 bytes x 4 plus the witness (item count, a 65-byte Schnorr signature with an explicit
    /// SIGHASH_ALL byte and its length byte) = 164 + 67.
    /// </summary>
    public const int P2TrInputWeight = 231;

    /// <summary>A P2WPKH output: 8-byte amount, 1-byte script length, 22-byte script, x 4.</summary>
    public const int P2WpkhOutputWeight = 124;

    /// <summary>The dust limit of a P2WPKH output (BOLT 3).</summary>
    public const long P2WpkhDustLimitSat = 294;

    /// <summary>The weight of a signed input of <paramref name="addressType"/>.</summary>
    public static int GetInputWeight(AddressType addressType) => addressType switch
    {
        AddressType.P2Wpkh => P2WpkhInputWeight,
        AddressType.P2Tr => P2TrInputWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(addressType), addressType, "Not a single address type")
    };
}