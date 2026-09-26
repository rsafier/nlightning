namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Protocol.Models;

/// <summary>
/// A commitment the classifier compares a funding spend with: its number and the txid of its rebuilt transaction
/// (<c>CommitmentOutputMapper</c> in Infrastructure.Bitcoin rebuilds it from the persisted spec).
/// </summary>
public readonly record struct CommitmentCandidate(ulong Number, TxId TxId);

/// <summary>
/// What <see cref="Classifiers.FundingSpendClassifier"/> knows about a channel when its funding output is spent.
/// </summary>
/// <param name="FundingTxId">The funding transaction id.</param>
/// <param name="FundingOutputIndex">The funding output index.</param>
/// <param name="CommitmentNumber">The channel's obscuring helper (opener payment basepoint first).</param>
/// <param name="LocalCommit">Our latest local commitment (the one we could broadcast), if any.</param>
/// <param name="RemoteCommit">The peer's current commitment. Every peer commitment numbered below it is revoked.
/// Null only before the first commitment exchange.</param>
/// <param name="RemoteNextCommit">The peer's commitment we signed whose <c>revoke_and_ack</c> is outstanding.</param>
/// <param name="MutualCloseTxIds">Closing transactions we signed (every fee we proposed or accepted).</param>
/// <param name="LocalShutdownScript">Our <c>shutdown</c> scriptpubkey, if a shutdown was exchanged.</param>
/// <param name="RemoteShutdownScript">The peer's <c>shutdown</c> scriptpubkey, if a shutdown was exchanged.</param>
public sealed record FundingSpendContext(
    TxId FundingTxId,
    uint FundingOutputIndex,
    CommitmentNumber CommitmentNumber,
    CommitmentCandidate? LocalCommit,
    CommitmentCandidate? RemoteCommit,
    CommitmentCandidate? RemoteNextCommit = null,
    IReadOnlyCollection<TxId>? MutualCloseTxIds = null,
    byte[]? LocalShutdownScript = null,
    byte[]? RemoteShutdownScript = null);