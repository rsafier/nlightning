using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Outputs;

/// <summary>
/// Simple taproot channels on chain (NL-966): the key-path signature of one of our P2TR anchors in a CPFP child.
/// </summary>
public partial class LocalLightningSigner
{
    /// <inheritdoc />
    public CompactSignature SignTaprootAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction,
                                                   int inputIndex, CompactPubKey? ourPerCommitmentPoint,
                                                   IReadOnlyList<SpentOutput> spentOutputs)
    {
        ArgumentNullException.ThrowIfNull(unsignedTransaction);
        ArgumentNullException.ThrowIfNull(spentOutputs);
        var signingInfo = GetRegisteredSigningInfo(channelId);
        ThrowIfNotTaproot(channelId, signingInfo, "sign a taproot anchor input");

        Transaction tx;
        try
        {
            tx = Transaction.Load(unsignedTransaction.RawTxBytes, _network);
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to load the anchor child transaction", channelId, e, "Internal error");
        }

        if (inputIndex < 0 || inputIndex >= tx.Inputs.Count)
            throw new SignerException($"The anchor child transaction has no input {inputIndex}", channelId,
                                      "Internal error");
        if (spentOutputs.Count != tx.Inputs.Count)
            throw new SignerException($"A taproot signature needs every spent output: {spentOutputs.Count} given for "
                                    + $"{tx.Inputs.Count} inputs", channelId, "Internal error");

        // Our to_local_anchor: the delayed key at our point; our to_remote_anchor on the peer's commitment: the payment
        // basepoint (option_static_remotekey)
        using var key = ourPerCommitmentPoint is { } point
                            ? DeriveDelayedKey(signingInfo.ChannelKeyIndex, point)
                            : GetPaymentBasepointSecret(signingInfo.ChannelKeyIndex);
        var anchor = new TaprootAnchorOutput(TransactionConstants.AnchorOutputAmount, key.PubKey);

        // The spent output must be exactly that anchor, at the input's outpoint
        var spent = spentOutputs[inputIndex];
        var prevOut = tx.Inputs[inputIndex].PrevOut;
        if (spent.Amount != TransactionConstants.AnchorOutputAmount
         || !((byte[])spent.ScriptPubKey).AsSpan().SequenceEqual(anchor.ScriptPubKey.ToBytes())
         || spent.Index != prevOut.N || new uint256((byte[])spent.TxId) != prevOut.Hash)
            throw new SignerException($"Input {inputIndex} does not spend our taproot anchor", channelId,
                                      "Internal error");

        var txOuts = spentOutputs.Select(o => new TxOut(Money.Satoshis(o.Amount.Satoshi),
                                                        new Script((byte[])o.ScriptPubKey)))
                                 .ToArray();
        var sigHash = tx.GetSignatureHashTaproot(txOuts, new TaprootExecutionData(inputIndex)
        {
            SigHash = TaprootSigHash.Default
        });

        // BIP 341 key path: the internal key tweaked with the anchor's tapscript root
        var keyPair = key.CreateTaprootKeyPair(anchor.Tree.MerkleRoot);
        var signature = keyPair.SignTaprootKeySpend(sigHash, TaprootSigHash.Default);
        if (!anchor.Tree.OutputKey.VerifySignature(sigHash, signature.SchnorrSignature))
            throw new SignerException("Our BIP 340 anchor signature does not verify", channelId, "Internal error");

        return new CompactSignature(signature.SchnorrSignature.ToBytes());
    }

    private Key DeriveDelayedKey(uint channelKeyIndex, CompactPubKey perCommitmentPoint)
    {
        using var basepointSecret = GetDelayedPaymentBasepointSecret(channelKeyIndex);
        return CreateAndWipe(_keyDerivationService.DerivePrivateKey(basepointSecret.ToBytes(), perCommitmentPoint));
    }
}