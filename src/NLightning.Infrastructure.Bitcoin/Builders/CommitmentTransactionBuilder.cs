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
using Outputs;

public class CommitmentTransactionBuilder : ICommitmentTransactionBuilder
{
    private readonly Network _network;

    public CommitmentTransactionBuilder(IOptions<NodeOptions> nodeOptions)
    {
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
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
        var outputs = new List<(BaseOutput Output, HtlcOutputInfo? Htlc)>();

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
            var hasAnchors = transaction.LocalAnchorOutput != null || transaction.RemoteAnchorOutput != null;
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
            var hasAnchors = transaction.LocalAnchorOutput != null || transaction.RemoteAnchorOutput != null;
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
            var hasAnchors = transaction.LocalAnchorOutput != null || transaction.RemoteAnchorOutput != null;
            var receivedHtlc = new ReceivedHtlcOutput(htlcOutput.Amount, htlcOutput.CltvExpiry, hasAnchors,
                                                      new PubKey(htlcOutput.LocalHtlcPubKey), htlcOutput.PaymentHash,
                                                      new PubKey(htlcOutput.RemoteHtlcPubKey),
                                                      new PubKey(htlcOutput.RevocationPubKey));

            outputs.Add((receivedHtlc, htlcOutput));
        }

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
}