using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Onchain.Models;
using Taproot;

/// <summary>
/// Simple taproot channels on chain (NL-877 T4 safety floor): the BIP 341 script-path signature of a sweep input that
/// spends a tapscript leaf of one of our commitment outputs (our <c>to_local</c> delay leaf, our <c>to_remote</c> leaf
/// on the peer's commitment, the revocation leaf of a revoked <c>to_local</c>).
/// </summary>
public partial class LocalLightningSigner
{
    private const int TaprootXOnlyKeyLength = 32;

    /// <summary>
    /// BIP 340 signature (64 bytes, <c>SIGHASH_DEFAULT</c>) of input <see cref="SweepSigningContext.InputIndex"/> of
    /// <paramref name="tx"/> over the BIP 341 tapscript sighash of the leaf <see cref="SweepSigningContext.WitnessScript"/>
    /// with every spent output; the leaf must check the derived key (x-only), the spent output must be the P2TR output
    /// named by the context, and the signature is verified before it is returned.
    /// </summary>
    private CompactSignature SignTaprootSweepInput(ChannelId channelId, Transaction tx, SweepSigningContext context,
                                                   Key key, IReadOnlyList<SpentOutput> spentOutputs)
    {
        if (context.WitnessScript is null)
            throw new SignerException("A taproot script-path spend needs its tapscript leaf", channelId,
                                      "Internal error");
        if (spentOutputs.Count != tx.Inputs.Count)
            throw new SignerException($"A taproot signature needs every spent output: {spentOutputs.Count} given for "
                                    + $"{tx.Inputs.Count} inputs", channelId, "Internal error");

        var spent = spentOutputs[context.InputIndex];
        var spentScript = (byte[])spent.ScriptPubKey;
        if ((ulong)spent.Amount.Satoshi != context.AmountSat || spentScript is not [0x51, 0x20, ..]
         || spentScript.Length != 2 + TaprootXOnlyKeyLength)
            throw new SignerException($"Input {context.InputIndex} does not spend a P2TR output of {context.AmountSat} sat",
                                      channelId, "Internal error");

        // The leaf must check the derived key, so a wrong key kind, point or secret never yields a signature
        var leafScript = new Script(context.WitnessScript);
        var xOnly = SimpleTaprootScripts.XOnly(key.PubKey);
        if (!leafScript.ToOps().Any(op => op.PushData is { } data && data.AsSpan().SequenceEqual(xOnly)))
            throw new SignerException(
                $"The tapscript leaf does not contain the {context.KeyKind} key of input {context.InputIndex}",
                channelId, "Internal error");

        var leaf = new TapScript(leafScript, SimpleTaprootScripts.LeafVersion);
        var txOuts = spentOutputs.Select(o => new TxOut(Money.Satoshis(o.Amount.Satoshi),
                                                        new Script((byte[])o.ScriptPubKey)))
                                 .ToArray();
        var sigHash = tx.GetSignatureHashTaproot(txOuts, new TaprootExecutionData(context.InputIndex, leaf.LeafHash)
        {
            SigHash = TaprootSigHash.Default
        });

        var signature = TaprootSignatures.Sign(key, sigHash);
        if (!TaprootSignatures.Verify(key.PubKey, sigHash, signature))
            throw new SignerException("Our BIP 340 sweep signature does not verify", channelId, "Internal error");

        return new CompactSignature(signature);
    }
}