namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;

/// <summary>
/// Shared Input Signature TLV.
/// </summary>
/// <remarks>
/// BOLT 2 <c>tx_signatures_tlvs</c> type 0: [<c>signature</c>:<c>signature</c>] (IT-W-03), the sender's 64-byte
/// compact ECDSA signature (SIGHASH_ALL) of the shared 2-of-2 funding input of a splice. It is not part of the
/// <c>witnesses</c> list, which only covers the inputs the sender added itself. The converter is lane IT-C's (IT3-T1).
/// </remarks>
public sealed class SharedInputSignatureTlv : BaseTlv
{
    /// <summary>
    /// The size of the TLV value: a 64-byte compact signature.
    /// </summary>
    public const int ValueLength = CryptoConstants.MaxSignatureSize;

    /// <summary>
    /// The signature of the shared input.
    /// </summary>
    public CompactSignature Signature { get; }

    /// <exception cref="ArgumentException">The signature is not 64 bytes.</exception>
    public SharedInputSignatureTlv(CompactSignature signature) : base(InteractiveTxTlvConstants.SharedInputSignature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != ValueLength)
            throw new ArgumentException($"shared_input_signature must be {ValueLength} bytes", nameof(signature));

        Signature = signature;

        Value = [.. signature.Value];
        Length = Value.Length;
    }
}