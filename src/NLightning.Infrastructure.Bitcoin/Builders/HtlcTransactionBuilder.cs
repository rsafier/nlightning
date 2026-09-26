using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Interfaces;
using Outputs;

/// <summary>
/// Builds BOLT 3 HTLC-timeout/HTLC-success transactions from a Domain <see cref="HtlcTransactionModel"/>.
/// </summary>
public class HtlcTransactionBuilder : IHtlcTransactionBuilder
{
    private readonly Network _network;

    public HtlcTransactionBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
    }

    /// <inheritdoc />
    public HtlcTransactionBuildResult Build(HtlcTransactionModel transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var spentOutput = CreateSpentOutput(transaction.SpentOutput, transaction.HasAnchors);

        var tx = Transaction.Create(_network);
        tx.Version = TransactionConstants.HtlcTransactionVersion;
        tx.LockTime = new LockTime(transaction.LockTime);
        tx.Inputs.Add(new OutPoint(new uint256(transaction.CommitmentTxId), transaction.CommitmentOutputIndex), null,
                      null, new Sequence(transaction.Sequence));

        var output = new HtlcResolutionOutput(transaction.OutputAmount, new PubKey(transaction.LocalDelayedPubKey),
                                              new PubKey(transaction.RevocationPubKey), transaction.ToSelfDelay);
        tx.Outputs.Add(output.ToTxOut());

        return new HtlcTransactionBuildResult(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                                              spentOutput.RedeemScript.ToBytes(),
                                              LightningMoney.Satoshis(spentOutput.Amount.Satoshi));
    }

    /// <inheritdoc />
    public SignedTransaction AddWitness(HtlcTransactionModel transaction, HtlcTransactionBuildResult buildResult,
                                        CompactSignature remoteHtlcSignature, CompactSignature localHtlcSignature,
                                        byte[]? paymentPreimage = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(remoteHtlcSignature);
        ArgumentNullException.ThrowIfNull(localHtlcSignature);

        byte[] preimagePush;
        if (transaction.Type == HtlcTransactionType.Success)
        {
            if (paymentPreimage is not { Length: CryptoConstants.Sha256HashLen })
                throw new ArgumentException("HTLC-success needs the 32-byte payment preimage", nameof(paymentPreimage));
            preimagePush = paymentPreimage;
        }
        else
        {
            if (paymentPreimage is not null)
                throw new ArgumentException("HTLC-timeout takes no preimage", nameof(paymentPreimage));
            preimagePush = [];
        }

        // BOLT 3 / BOLT 5: with option_anchors the remote HTLC signature is SIGHASH_SINGLE|SIGHASH_ANYONECANPAY
        var remoteSigHash = transaction.HasAnchors ? SigHash.Single | SigHash.AnyoneCanPay : SigHash.All;
        var remoteSignature = ToTransactionSignature(remoteHtlcSignature, remoteSigHash, nameof(remoteHtlcSignature));
        var localSignature = ToTransactionSignature(localHtlcSignature, SigHash.All, nameof(localHtlcSignature));

        var tx = Transaction.Load(buildResult.Transaction.RawTxBytes, _network);
        tx.Inputs[0].WitScript = new WitScript(new[]
        {
            Array.Empty<byte>(), remoteSignature.ToBytes(), localSignature.ToBytes(), preimagePush,
            (byte[])buildResult.SpentWitnessScript
        });

        return new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes(),
                                     [remoteHtlcSignature, localHtlcSignature]);
    }

    private static BaseHtlcOutput CreateSpentOutput(HtlcOutputInfo htlcOutput, bool hasAnchors)
    {
        return htlcOutput switch
        {
            OfferedHtlcOutputInfo offered => new OfferedHtlcOutput(offered.Amount, offered.CltvExpiry, hasAnchors,
                                                                   new PubKey(offered.LocalHtlcPubKey),
                                                                   offered.PaymentHash,
                                                                   new PubKey(offered.RemoteHtlcPubKey),
                                                                   new PubKey(offered.RevocationPubKey)),
            ReceivedHtlcOutputInfo received => new ReceivedHtlcOutput(received.Amount, received.CltvExpiry,
                                                                      hasAnchors,
                                                                      new PubKey(received.LocalHtlcPubKey),
                                                                      received.PaymentHash,
                                                                      new PubKey(received.RemoteHtlcPubKey),
                                                                      new PubKey(received.RevocationPubKey)),
            _ => throw new ArgumentException($"Unsupported HTLC output type {htlcOutput.GetType().Name}",
                                             nameof(htlcOutput))
        };
    }

    private static TransactionSignature ToTransactionSignature(CompactSignature signature, SigHash sigHash,
                                                               string paramName)
    {
        if (!ECDSASignature.TryParseFromCompact(signature, out var ecdsaSignature))
            throw new ArgumentException("Invalid compact signature", paramName);

        return new TransactionSignature(ecdsaSignature, sigHash);
    }
}