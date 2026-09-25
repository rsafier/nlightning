using NLightning.Domain.Bitcoin.Transactions.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Models;

namespace NLightning.Domain.Bitcoin.Transactions.Models;

/// <summary>
/// Represents a commitment transaction in the domain model.
/// This class encapsulates the logical structure of a Lightning Network commitment transaction
/// as defined by BOLT specifications, without dependencies on specific Bitcoin libraries.
/// </summary>
public class CommitmentTransactionModel
{
    /// <summary>
    /// Gets the funding outpoint that this commitment transaction spends.
    /// </summary>
    public FundingOutputInfo FundingOutput { get; }

    /// <summary>
    /// Gets the channel's commitment number obscuring helper.
    /// </summary>
    public CommitmentNumber CommitmentNumber { get; }

    /// <summary>
    /// Gets the commitment number of this transaction (the holder's commitment number, not an index).
    /// </summary>
    public ulong Number { get; }

    /// <summary>
    /// Gets or sets the transaction ID after the transaction is constructed.
    /// </summary>
    public TxId? TransactionId { get; set; }

    /// <summary>
    /// Gets the to_local output, if present.
    /// </summary>
    public ToLocalOutputInfo? ToLocalOutput { get; }

    /// <summary>
    /// Gets the to_remote output, if present.
    /// </summary>
    public ToRemoteOutputInfo? ToRemoteOutput { get; }

    /// <summary>
    /// Gets the local anchor output, if present.
    /// </summary>
    public AnchorOutputInfo? LocalAnchorOutput { get; }

    /// <summary>
    /// Gets the remote anchor output, if present.
    /// </summary>
    public AnchorOutputInfo? RemoteAnchorOutput { get; }

    /// <summary>
    /// Gets the list of offered HTLC outputs.
    /// </summary>
    public IReadOnlyList<OfferedHtlcOutputInfo> OfferedHtlcOutputs { get; }

    /// <summary>
    /// Gets the list of received HTLC outputs.
    /// </summary>
    public IReadOnlyList<ReceivedHtlcOutputInfo> ReceivedHtlcOutputs { get; }

    /// <summary>
    /// Gets the base fee of this transaction (BOLT 3 "base fee"; trimmed HTLC value is extra fee not included here).
    /// </summary>
    public LightningMoney Fee { get; }

    /// <summary>
    /// Gets the feerate (sat per 1000 weight) the commitment was built with. HTLC transactions spending its HTLC outputs
    /// use the same feerate.
    /// </summary>
    public ulong FeeRatePerKw { get; init; }

    /// <summary>
    /// Gets whether option_anchors applies (HTLC scripts with <c>1 OP_CSV</c>, zero-fee HTLC transactions with
    /// sequence 1 and <c>SIGHASH_SINGLE|SIGHASH_ANYONECANPAY</c> remote HTLC signatures).
    /// </summary>
    public bool HasAnchors { get; init; }

    /// <summary>
    /// Gets the CSV delay on the holder's delayed outputs (to_local and the HTLC transaction outputs).
    /// </summary>
    public ushort ToSelfDelay { get; init; }

    /// <summary>
    /// Gets the holder's <c>local_delayedpubkey</c> for this commitment. HTLC transaction outputs pay to it after
    /// <see cref="ToSelfDelay"/>. Null only for models built without the factory.
    /// </summary>
    public CompactPubKey? LocalDelayedPubKey { get; init; }

    /// <summary>
    /// Gets the <c>revocationpubkey</c> for this commitment (the counterparty's penalty key). Null only for models built
    /// without the factory.
    /// </summary>
    public CompactPubKey? RevocationPubKey { get; init; }

    /// <summary>
    /// Gets the holder's per-commitment point for this commitment: every HTLC key of the commitment and of its HTLC
    /// transactions is derived from it. Null only for models built without the factory.
    /// </summary>
    public CompactPubKey? PerCommitmentPoint { get; init; }

    /// <summary>
    /// Creates a new instance of CommitmentTransactionModel.
    /// </summary>
    /// <param name="commitmentNumber">The channel's obscuring helper.</param>
    /// <param name="number">The commitment number of this transaction.</param>
    /// <param name="fee">The commitment transaction fee.</param>
    /// <param name="fundingOutput">The funding output spent by the commitment transaction.</param>
    /// <param name="localAnchorOutput">The local anchor output, if any.</param>
    /// <param name="remoteAnchorOutput">The remote anchor output, if any.</param>
    /// <param name="toLocalOutput">The to_local output, if any.</param>
    /// <param name="toRemoteOutput">The to_remote output, if any.</param>
    /// <param name="offeredHtlcOutputs">The offered HTLC outputs.</param>
    /// <param name="receivedHtlcOutputs">The received HTLC outputs.</param>
    public CommitmentTransactionModel(CommitmentNumber commitmentNumber, ulong number, LightningMoney fee,
                                      FundingOutputInfo fundingOutput, AnchorOutputInfo? localAnchorOutput = null,
                                      AnchorOutputInfo? remoteAnchorOutput = null,
                                      ToLocalOutputInfo? toLocalOutput = null,
                                      ToRemoteOutputInfo? toRemoteOutput = null,
                                      IEnumerable<OfferedHtlcOutputInfo>? offeredHtlcOutputs = null,
                                      IEnumerable<ReceivedHtlcOutputInfo>? receivedHtlcOutputs = null)
    {
        if (fundingOutput.TransactionId is null || fundingOutput.TransactionId.Value == TxId.Zero)
            throw new ArgumentException("Funding output must have a valid transaction ID.", nameof(fundingOutput));

        FundingOutput = fundingOutput;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, CommitmentNumber.MaxValue);

        CommitmentNumber = commitmentNumber;
        Number = number;
        Fee = fee;
        ToLocalOutput = toLocalOutput;
        ToRemoteOutput = toRemoteOutput;
        LocalAnchorOutput = localAnchorOutput;
        RemoteAnchorOutput = remoteAnchorOutput;
        OfferedHtlcOutputs = (offeredHtlcOutputs ?? []).ToList();
        ReceivedHtlcOutputs = (receivedHtlcOutputs ?? []).ToList();
    }

    /// <summary>
    /// Gets the Bitcoin locktime for this commitment transaction, derived from the commitment number.
    /// </summary>
    public BitcoinLockTime GetLockTime() => CommitmentNumber.LockTime(Number);

    /// <summary>
    /// Gets the Bitcoin sequence for this commitment transaction, derived from the commitment number.
    /// </summary>
    public BitcoinSequence GetSequence() => CommitmentNumber.Sequence(Number);

    /// <summary>
    /// Gets all outputs of this commitment transaction.
    /// </summary>
    public IEnumerable<IOutputInfo> GetAllOutputs()
    {
        if (ToLocalOutput != null) yield return ToLocalOutput;
        if (ToRemoteOutput != null) yield return ToRemoteOutput;
        if (LocalAnchorOutput != null) yield return LocalAnchorOutput;
        if (RemoteAnchorOutput != null) yield return RemoteAnchorOutput;

        foreach (var output in OfferedHtlcOutputs) yield return output;
        foreach (var output in ReceivedHtlcOutputs) yield return output;
    }
}