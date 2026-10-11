using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Builders;

using Comparers;
using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Node.Options;
using Interfaces;
using Networks;
using Outputs;

public class CommitmentTransactionBuilder : ICommitmentTransactionBuilder
{
    private readonly Network _network;

    public CommitmentTransactionBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <inheritdoc />
    public SignedTransaction Build(CommitmentTransactionModel transaction) =>
        BuildWithOutputMap(transaction).Transaction;

    /// <inheritdoc />
    public CommitmentTransactionBuildResult BuildWithOutputMap(CommitmentTransactionModel transaction)
    {
        if (transaction.FundingOutput.TransactionId is null || transaction.FundingOutput.Index is null)
            throw new ArgumentException("Funding output must have a valid transaction Id and index.");

        // Create a new Bitcoin transaction
        var tx = Transaction.Create(_network);

        // Set the transaction version as per BOLT spec
        tx.Version = TransactionConstants.CommitmentTransactionVersion;

        // Set lock time derived from the commitment number
        tx.LockTime = new LockTime(transaction.GetLockTime());

        // Create an out-point for the funding transaction
        var outpoint = new OutPoint(new uint256(transaction.FundingOutput.TransactionId),
                                    transaction.FundingOutput.Index.Value);
        // Set the sequence number derived from the commitment number
        tx.Inputs.Add(outpoint, null, null, new Sequence(transaction.GetSequence()));

        // Collect all outputs, remembering which HTLC each HTLC output came from
        var outputs = transaction.IsSimpleTaproot
                          ? CreateSimpleTaprootOutputs(transaction)
                          : CreateOutputs(transaction);

        // BOLT 3 ordering: BIP 69 (amount, scriptPubKey), HTLC ties broken by cltv_expiry. OrderBy is stable, so
        // fully identical outputs keep the model's order and the HTLC map stays deterministic.
        var sortedOutputs = outputs.OrderBy(o => o.Output, TransactionOutputComparer.Instance).ToList();

        // Add sorted outputs to the transaction and record where each HTLC landed
        var htlcOutputsInTxOrder = new List<(HtlcOutputInfo Output, uint Vout)>();
        for (var vout = 0; vout < sortedOutputs.Count; vout++)
        {
            var (output, htlc) = sortedOutputs[vout];
            tx.Outputs.Add(output.ToTxOut());
            if (htlc is not null)
                htlcOutputsInTxOrder.Add((htlc, (uint)vout));
        }

        return new CommitmentTransactionBuildResult(new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes()),
                                                    htlcOutputsInTxOrder);
    }

    private static List<(BaseOutput Output, HtlcOutputInfo? Htlc)> CreateOutputs(
        CommitmentTransactionModel transaction)
    {
        var outputs = new List<(BaseOutput Output, HtlcOutputInfo? Htlc)>();
        var hasAnchors = transaction.LocalAnchorOutput != null || transaction.RemoteAnchorOutput != null;

        // Convert and add to_local output if present
        if (transaction.ToLocalOutput != null)
        {
            var toLocalOutput = new ToLocalOutput(transaction.ToLocalOutput.Amount,
                                                  new PubKey(transaction.ToLocalOutput.LocalDelayedPaymentPubKey),
                                                  new PubKey(transaction.ToLocalOutput.RevocationPubKey),
                                                  transaction.ToLocalOutput.ToSelfDelay);

            outputs.Add((toLocalOutput, null));
        }

        // Convert and add to_remote output if present
        if (transaction.ToRemoteOutput != null)
        {
            var toRemoteOutput = new ToRemoteOutput(transaction.ToRemoteOutput.Amount, hasAnchors,
                                                    new PubKey(transaction.ToRemoteOutput.RemotePaymentPubKey));

            outputs.Add((toRemoteOutput, null));
        }

        // Convert and add local anchor output if present
        if (transaction.LocalAnchorOutput != null)
        {
            var localAnchorOutput = new ToAnchorOutput(transaction.LocalAnchorOutput.Amount,
                                                       new PubKey(transaction.LocalAnchorOutput.FundingPubKey));

            outputs.Add((localAnchorOutput, null));
        }

        // Convert and add remote anchor output if present
        if (transaction.RemoteAnchorOutput != null)
        {
            var remoteAnchorOutput = new ToAnchorOutput(transaction.RemoteAnchorOutput.Amount,
                                                        new PubKey(transaction.RemoteAnchorOutput.FundingPubKey));

            outputs.Add((remoteAnchorOutput, null));
        }

        // Convert and add offered HTLC outputs
        foreach (var htlcOutput in transaction.OfferedHtlcOutputs)
        {
            var offeredHtlc = new OfferedHtlcOutput(htlcOutput.Amount, htlcOutput.CltvExpiry, hasAnchors,
                                                    new PubKey(htlcOutput.LocalHtlcPubKey),
                                                    htlcOutput.PaymentHash,
                                                    new PubKey(htlcOutput.RemoteHtlcPubKey),
                                                    new PubKey(htlcOutput.RevocationPubKey));

            outputs.Add((offeredHtlc, htlcOutput));
        }

        // Convert and add received HTLC outputs
        foreach (var htlcOutput in transaction.ReceivedHtlcOutputs)
        {
            var receivedHtlc = new ReceivedHtlcOutput(htlcOutput.Amount, htlcOutput.CltvExpiry, hasAnchors,
                                                      new PubKey(htlcOutput.LocalHtlcPubKey), htlcOutput.PaymentHash,
                                                      new PubKey(htlcOutput.RemoteHtlcPubKey),
                                                      new PubKey(htlcOutput.RevocationPubKey));

            outputs.Add((receivedHtlc, htlcOutput));
        }

        return outputs;
    }

    /// <summary>
    /// The P2TR outputs of an <c>option_simple_taproot</c> commitment (bolt-simple-taproot.md §Commitment Transactions):
    /// to_local and to_remote on the NUMS internal key, the anchors keyed to their owner's key (the model's
    /// <see cref="AnchorOutputInfo.FundingPubKey"/> holds <c>local_delayedpubkey</c>/<c>remotepubkey</c>), HTLC outputs
    /// on the revocation key. The ordering and the HTLC map are those of any commitment.
    /// </summary>
    internal static List<(BaseOutput Output, HtlcOutputInfo? Htlc)> CreateSimpleTaprootOutputs(
        CommitmentTransactionModel transaction)
    {
        var outputs = new List<(BaseOutput Output, HtlcOutputInfo? Htlc)>();

        if (transaction.ToLocalOutput is { } toLocal)
            outputs.Add((new TaprootToLocalOutput(toLocal.Amount, new PubKey(toLocal.LocalDelayedPaymentPubKey),
                                                  new PubKey(toLocal.RevocationPubKey), toLocal.ToSelfDelay), null));

        if (transaction.ToRemoteOutput is { } toRemote)
            outputs.Add((new TaprootToRemoteOutput(toRemote.Amount, new PubKey(toRemote.RemotePaymentPubKey)), null));

        if (transaction.LocalAnchorOutput is { } localAnchor)
            outputs.Add((new TaprootAnchorOutput(localAnchor.Amount, new PubKey(localAnchor.FundingPubKey)), null));

        if (transaction.RemoteAnchorOutput is { } remoteAnchor)
            outputs.Add((new TaprootAnchorOutput(remoteAnchor.Amount, new PubKey(remoteAnchor.FundingPubKey)), null));

        foreach (var htlc in transaction.OfferedHtlcOutputs)
            outputs.Add((new TaprootOfferedHtlcOutput(htlc.Amount, htlc.CltvExpiry, new PubKey(htlc.LocalHtlcPubKey),
                                                      htlc.PaymentHash, new PubKey(htlc.RemoteHtlcPubKey),
                                                      new PubKey(htlc.RevocationPubKey)), htlc));

        foreach (var htlc in transaction.ReceivedHtlcOutputs)
            outputs.Add((new TaprootReceivedHtlcOutput(htlc.Amount, htlc.CltvExpiry,
                                                       new PubKey(htlc.LocalHtlcPubKey), htlc.PaymentHash,
                                                       new PubKey(htlc.RemoteHtlcPubKey),
                                                       new PubKey(htlc.RevocationPubKey)), htlc));

        return outputs;
    }
}