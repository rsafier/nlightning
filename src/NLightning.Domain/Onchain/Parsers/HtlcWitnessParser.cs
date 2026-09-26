using System.Security.Cryptography;

namespace NLightning.Domain.Onchain.Parsers;

using Bitcoin.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// The spend path of an HTLC output witness and, for the preimage paths, the 32-byte preimage candidate.
/// </summary>
/// <param name="Path">The script path the witness used.</param>
/// <param name="Preimage">The 32-byte item in the preimage position (not yet checked against a payment hash).</param>
public sealed record HtlcSpendWitness(HtlcSpendPath Path, Secret? Preimage);

/// <summary>
/// Preimage extraction from on-chain HTLC spends (BOLT 5 plan O3-T5; B5-LCL-LO-01, B5-RMT-LO-01, B5-REV-07): the peer's
/// HTLC-success transaction (5-item witness) and its direct preimage claim of an offered output (3-item witness) carry
/// the payment preimage.
/// </summary>
/// <remarks>
/// The witness shape alone chooses the path; a preimage is only reported by <see cref="TryExtractPreimage(IReadOnlyList{byte[]}, Hash, out Secret)"/>
/// when <c>SHA256(preimage) == payment_hash</c> of the HTLC, so a forged or unrelated 32-byte item is never used
/// (script validity itself is the chain's job: only confirmed, valid transactions reach us). Never throws for a
/// malformed witness.
/// </remarks>
public static class HtlcWitnessParser
{
    private const int CompressedPubKeyLength = 33;

    /// <summary>Reads the spend path from a witness stack (the witness script last).</summary>
    public static HtlcSpendWitness Parse(IReadOnlyList<byte[]>? witness)
    {
        if (witness is null || witness.Any(item => item is null))
            return new HtlcSpendWitness(HtlcSpendPath.Unknown, null);

        switch (witness.Count)
        {
            // 0 <remotehtlcsig> <localhtlcsig> <payment_preimage | <>> <witnessScript>
            case 5 when witness[0].Length == 0 && IsSignature(witness[1]) && IsSignature(witness[2]):
                return witness[3].Length switch
                {
                    CryptoConstants.SecretLen => new HtlcSpendWitness(HtlcSpendPath.HtlcSuccessTransaction,
                                                                      new Secret(witness[3].ToArray())),
                    0 => new HtlcSpendWitness(HtlcSpendPath.HtlcTimeoutTransaction, null),
                    _ => new HtlcSpendWitness(HtlcSpendPath.Unknown, null)
                };

            // <sig> <payment_preimage | <> | revocationpubkey> <witnessScript>
            case 3 when IsSignature(witness[0]):
                return witness[1].Length switch
                {
                    CryptoConstants.SecretLen => new HtlcSpendWitness(HtlcSpendPath.PreimageClaim,
                                                                      new Secret(witness[1].ToArray())),
                    0 => new HtlcSpendWitness(HtlcSpendPath.TimeoutClaim, null),
                    CompressedPubKeyLength => new HtlcSpendWitness(HtlcSpendPath.Revocation, null),
                    _ => new HtlcSpendWitness(HtlcSpendPath.Unknown, null)
                };

            default:
                return new HtlcSpendWitness(HtlcSpendPath.Unknown, null);
        }
    }

    /// <summary>
    /// The preimage of <paramref name="paymentHash"/> revealed by <paramref name="witness"/>, if it holds one.
    /// </summary>
    public static bool TryExtractPreimage(IReadOnlyList<byte[]>? witness, Hash paymentHash, out Secret preimage)
    {
        var parsed = Parse(witness);
        if (parsed.Preimage is { } candidate && Matches(candidate, paymentHash))
        {
            preimage = candidate;
            return true;
        }

        preimage = default;
        return false;
    }

    /// <summary>
    /// The preimage of <paramref name="paymentHash"/> revealed by the input of <paramref name="spender"/> that spends
    /// <paramref name="spentTxId"/>:<paramref name="spentVout"/> (the HTLC output), if any.
    /// </summary>
    public static bool TryExtractPreimage(ChainTx spender, TxId spentTxId, uint spentVout, Hash paymentHash,
                                          out Secret preimage)
    {
        ArgumentNullException.ThrowIfNull(spender);

        var index = spender.IndexOfInputSpending(spentTxId, spentVout);
        if (index >= 0)
            return TryExtractPreimage(spender.Inputs[index].Witness, paymentHash, out preimage);

        preimage = default;
        return false;
    }

    private static bool Matches(Secret candidate, Hash paymentHash)
    {
        Span<byte> hash = stackalloc byte[CryptoConstants.Sha256HashLen];
        SHA256.HashData((byte[])candidate, hash);
        return hash.SequenceEqual((byte[])paymentHash);
    }

    // DER signature plus the sighash byte: 9 to 73 bytes, starting with 0x30
    private static bool IsSignature(byte[] item) => item.Length is >= 9 and <= 73 && item[0] == 0x30;
}