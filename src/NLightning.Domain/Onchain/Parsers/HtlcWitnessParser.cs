using System.Security.Cryptography;

namespace NLightning.Domain.Onchain.Parsers;

using Bitcoin.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Enums;
using Models;
using Taproot;

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
/// <para>
/// Simple taproot HTLC outputs (NL-966) are read too, by their BIP 341 shapes: a script-path spend ends with
/// <c>&lt;leaf&gt; &lt;control_block&gt;</c> (an annex, the last item starting with <c>0x50</c>, is dropped first) and its
/// stack before the leaf picks the path: <c>&lt;sig&gt; &lt;sig&gt; &lt;preimage&gt;</c> the holder's HTLC-success
/// transaction, <c>&lt;sig&gt; &lt;sig&gt;</c> its HTLC-timeout transaction, <c>&lt;sig&gt; &lt;preimage&gt;</c> the
/// counterparty's preimage claim (success leaf of an offered output), <c>&lt;sig&gt;</c> the counterparty's timeout
/// claim (timeout leaf of an accepted output), where each signature is 64 bytes (65 with a sighash byte); a lone
/// signature is the revocation key path. Every preimage sits right before the leaf, so the API is the same for both
/// formats.
/// </para>
/// The witness shape alone chooses the path; a preimage is only reported by <see cref="TryExtractPreimage(IReadOnlyList{byte[]}, Hash, out Secret)"/>
/// when <c>SHA256(preimage) == payment_hash</c> of the HTLC, so a forged or unrelated 32-byte item is never used
/// (script validity itself is the chain's job: only confirmed, valid transactions reach us). Never throws for a
/// malformed witness.
/// </remarks>
public static class HtlcWitnessParser
{
    private const int CompressedPubKeyLength = 33;
    private const byte Annex = 0x50;

    /// <summary>Reads the spend path from a witness stack (the witness script last).</summary>
    public static HtlcSpendWitness Parse(IReadOnlyList<byte[]>? witness)
    {
        if (witness is null || witness.Any(item => item is null))
            return new HtlcSpendWitness(HtlcSpendPath.Unknown, null);

        if (TryParseTaproot(witness) is { } taproot)
            return taproot;

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

    /// <summary>
    /// The path of a simple taproot HTLC spend (see the remarks), or null when the witness is not a BIP 341 spend (the
    /// P2WSH shapes are read then). A P2WSH witness never ends with a control block: its witness script starts with
    /// <c>OP_DUP</c>, and its last item is never 33 + 32·m bytes with a <c>0xc0</c> leaf version.
    /// </summary>
    private static HtlcSpendWitness? TryParseTaproot(IReadOnlyList<byte[]> witness)
    {
        var items = witness.Count >= 2 && witness[^1] is [Annex, ..] ? witness.Take(witness.Count - 1).ToList()
                                                                      : witness;

        // Key path: <sig> alone (our penalty, or the peer's revocation of our own commitment)
        if (items.Count == 1)
            return IsSchnorrSignature(items[0]) ? new HtlcSpendWitness(HtlcSpendPath.Revocation, null) : null;

        if (items.Count < 3 || !TapscriptMerkleRoot.IsControlBlock(items[^1]))
            return null;

        var stack = items.Take(items.Count - 2).ToList();
        return stack switch
        {
            [var remote, var local, var preimage] when IsSchnorrSignature(remote) && IsSchnorrSignature(local)
                                                    && preimage.Length == CryptoConstants.SecretLen =>
                new HtlcSpendWitness(HtlcSpendPath.HtlcSuccessTransaction, new Secret(preimage.ToArray())),
            [var remote, var local] when IsSchnorrSignature(remote) && IsSchnorrSignature(local) =>
                new HtlcSpendWitness(HtlcSpendPath.HtlcTimeoutTransaction, null),
            [var signature, var preimage] when IsSchnorrSignature(signature)
                                            && preimage.Length == CryptoConstants.SecretLen =>
                new HtlcSpendWitness(HtlcSpendPath.PreimageClaim, new Secret(preimage.ToArray())),
            [var signature] when IsSchnorrSignature(signature) => new HtlcSpendWitness(HtlcSpendPath.TimeoutClaim,
                                                                                       null),
            _ => new HtlcSpendWitness(HtlcSpendPath.Unknown, null)
        };
    }

    private static bool Matches(Secret candidate, Hash paymentHash)
    {
        Span<byte> hash = stackalloc byte[CryptoConstants.Sha256HashLen];
        SHA256.HashData((byte[])candidate, hash);
        return hash.SequenceEqual((byte[])paymentHash);
    }

    // DER signature plus the sighash byte: 9 to 73 bytes, starting with 0x30
    private static bool IsSignature(byte[] item) => item.Length is >= 9 and <= 73 && item[0] == 0x30;

    // BIP 340 signature, 65 bytes with an explicit sighash byte
    private static bool IsSchnorrSignature(byte[] item) => item.Length is 64 or 65;
}