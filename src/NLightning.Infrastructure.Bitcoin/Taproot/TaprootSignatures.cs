using System.Security.Cryptography;
using NBitcoin;
using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Taproot;

using Crypto.Contexts;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Infrastructure.Crypto.Factories;

/// <summary>
/// BIP 341 sighashes and BIP 340 Schnorr signatures of the simple taproot channel transactions.
/// </summary>
/// <remarks>
/// The commitment transaction spends the MuSig2 funding output by key path (<c>SIGHASH_DEFAULT</c>); its sighash is
/// what the MuSig2 session signs. A second-level HTLC transaction spends its HTLC output by script path: the
/// counterparty signs with <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> (a 65-byte signature) and the holder with
/// <c>SIGHASH_DEFAULT</c> (64 bytes), both plain BIP 340 signatures with the HTLC keys.
/// </remarks>
public static class TaprootSignatures
{
    /// <summary>The counterparty's HTLC signature flag: <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> (0x83).</summary>
    public const TaprootSigHash CounterpartyHtlcSigHash = TaprootSigHash.Single | TaprootSigHash.AnyoneCanPay;

    /// <summary>The holder's own HTLC signature flag: <c>SIGHASH_DEFAULT</c> (no sighash byte).</summary>
    public const TaprootSigHash HolderHtlcSigHash = TaprootSigHash.Default;

    private const int AuxRandomnessLength = 32;
    private const int SchnorrSignatureLength = 64;

    /// <summary>
    /// The BIP 341 key-path sighash (<c>SIGHASH_DEFAULT</c>) of a simple taproot commitment (or any transaction whose
    /// only input spends the funding output): the 32-byte message both MuSig2 signers sign.
    /// </summary>
    /// <param name="unsignedTransaction">The unsigned transaction; its single input spends the funding output.</param>
    /// <param name="fundingAmount">The funding output's value.</param>
    /// <param name="fundingScriptPubKey">The funding output's scriptPubKey (<c>OP_1 &lt;funding_key&gt;</c>).</param>
    public static byte[] ComputeFundingKeySpendSigHash(SignedTransaction unsignedTransaction,
                                                       LightningMoney fundingAmount, BitcoinScript fundingScriptPubKey)
    {
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        ArgumentNullException.ThrowIfNull(fundingAmount);

        var tx = Transaction.Load(unsignedTransaction.RawTxBytes, Network.Main);
        if (tx.Inputs.Count != 1)
            throw new ArgumentException("The transaction must spend the funding output only", nameof(unsignedTransaction));

        var spent = new TxOut(Money.Satoshis(fundingAmount.Satoshi), new Script((byte[])fundingScriptPubKey));
        return tx.GetSignatureHashTaproot([spent], new TaprootExecutionData(0) { SigHash = TaprootSigHash.Default })
                 .ToBytes();
    }

    /// <summary>
    /// The BIP 341 script-path sighash of input 0 of a simple taproot HTLC transaction, which spends the leaf
    /// <see cref="HtlcTransactionBuildResult.SpentWitnessScript"/> of the output
    /// <see cref="HtlcTransactionBuildResult.SpentScriptPubKey"/>.
    /// </summary>
    public static uint256 ComputeHtlcSigHash(HtlcTransactionBuildResult built, TaprootSigHash sigHash,
                                             Network network)
    {
        ArgumentNullException.ThrowIfNull(built);
        if (!built.IsTaproot || built.SpentScriptPubKey is null)
            throw new ArgumentException("Not a simple taproot HTLC transaction", nameof(built));

        var tx = Transaction.Load(built.Transaction.RawTxBytes, network);
        if (tx.Inputs.Count != 1)
            throw new ArgumentException("A taproot HTLC transaction has exactly one input", nameof(built));

        var spent = new TxOut(Money.Satoshis(built.SpentAmount.Satoshi),
                              new Script((byte[])built.SpentScriptPubKey.Value));
        var leaf = new TapScript(new Script((byte[])built.SpentWitnessScript), SimpleTaprootScripts.LeafVersion);
        return tx.GetSignatureHashTaproot([spent], new TaprootExecutionData(0, leaf.LeafHash) { SigHash = sigHash });
    }

    /// <summary>
    /// A BIP 340 signature (64 bytes, without the sighash byte) of <paramref name="sigHash"/> by
    /// <paramref name="key"/>, with 32 bytes of fresh auxiliary randomness (BIP 340's side-channel hardening, NL-455).
    /// </summary>
    public static byte[] Sign(Key key, uint256 sigHash)
    {
        var auxRandomness = new byte[AuxRandomnessLength];
        try
        {
            using (var cryptoProvider = CryptoFactory.GetCryptoProvider())
                cryptoProvider.RandomBytes(auxRandomness);

            return Sign(key, sigHash, auxRandomness);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(auxRandomness);
        }
    }

    /// <summary>
    /// A BIP 340 signature with the given auxiliary randomness. 32 zero bytes give the deterministic signatures of the
    /// simple taproot vectors; production signing uses <see cref="Sign(Key, uint256)"/>.
    /// </summary>
    internal static byte[] Sign(Key key, uint256 sigHash, byte[] auxRandomness)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sigHash);
        ArgumentNullException.ThrowIfNull(auxRandomness);
        if (auxRandomness.Length != AuxRandomnessLength)
            throw new ArgumentException($"BIP 340 aux randomness is {AuxRandomnessLength} bytes",
                                        nameof(auxRandomness));

        var privateKey = key.ToBytes();
        try
        {
            if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey)
             || ecPrivKey is null)
                throw new ArgumentException("Invalid private key", nameof(key));

            using (ecPrivKey)
            {
                var signature = ecPrivKey.SignBIP340(sigHash.ToBytes(), auxRandomness);
                var bytes = new byte[SchnorrSignatureLength];
                signature.WriteToSpan(bytes);
                return bytes;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>
    /// Verifies a 64-byte BIP 340 signature of <paramref name="sigHash"/> by the x-only form of
    /// <paramref name="pubKey"/>; false (never a throw) for a malformed signature.
    /// </summary>
    public static bool Verify(PubKey pubKey, uint256 sigHash, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(pubKey);
        ArgumentNullException.ThrowIfNull(sigHash);
        if (signature.Length != SchnorrSignatureLength
         || !SecpSchnorrSignature.TryCreate(signature, out var schnorr) || schnorr is null)
            return false;

        if (!ECXOnlyPubKey.TryCreate(SimpleTaprootScripts.XOnly(pubKey), NLightningCryptoContext.Instance,
                                     out var xOnly) || xOnly is null)
            return false;

        return xOnly.SigVerifyBIP340(schnorr, sigHash.ToBytes());
    }

    /// <summary>The witness item of a signature: the 64 bytes, plus the sighash byte unless it is the default.</summary>
    public static byte[] ToWitnessSignature(ReadOnlySpan<byte> signature, TaprootSigHash sigHash)
    {
        if (signature.Length != SchnorrSignatureLength)
            throw new ArgumentException("A BIP 340 signature is 64 bytes", nameof(signature));

        return sigHash == TaprootSigHash.Default
                   ? signature.ToArray()
                   : [.. signature, (byte)sigHash];
    }
}