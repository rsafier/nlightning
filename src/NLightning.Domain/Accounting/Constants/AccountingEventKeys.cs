namespace NLightning.Domain.Accounting.Constants;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The event keys of the accounting feed. A key names a fact, derived from the fact alone, so a writer that runs twice
/// for one fact (a replay, a retry after a crash) writes the same key and the sealer keeps only the first row.
/// </summary>
/// <remarks>Txids are in display order (as bitcoind shows them); the other ids are lowercase hex.</remarks>
public static class AccountingEventKeys
{
    public const int MaxLength = 200;

    public static string InvoiceSettled(Hash paymentHash) => $"inv:{paymentHash}:settled";

    public static string PaymentSucceeded(Hash paymentHash) => $"pay:{paymentHash}:succeeded";

    /// <param name="paymentHash">The payment's hash.</param>
    /// <param name="attempt">What tells the failures of one hash apart (the payment's creation ticks).</param>
    public static string PaymentFailed(Hash paymentHash, long attempt) => $"pay:{paymentHash}:failed:{attempt}";

    public static string ForwardSettled(ChannelId incomingChannelId, ulong incomingHtlcId) =>
        $"fwd:{incomingChannelId}:{incomingHtlcId}:settled";

    public static string ForwardLostOnchain(ChannelId incomingChannelId, ulong incomingHtlcId) =>
        $"fwd:{incomingChannelId}:{incomingHtlcId}:onchain";

    /// <summary>The loss of one incoming HTLC of our settled invoice <paramref name="paymentHash"/> on chain
    /// (NL-688): one key per HTLC, so the parts of a multi-part payment are told apart.</summary>
    public static string InvoiceLostOnchain(Hash paymentHash, ChannelId incomingChannelId, ulong incomingHtlcId) =>
        $"inv:{paymentHash}:{incomingChannelId}:{incomingHtlcId}:onchain";

    public static string ChannelFunded(ChannelId channelId, TxId fundingTxId) =>
        $"chan:{channelId}:funded:{fundingTxId}";

    public static string Push(ChannelId channelId) => $"chan:{channelId}:push";

    public static string SpliceLocked(ChannelId channelId, TxId spliceTxId) => $"chan:{channelId}:splice:{spliceTxId}";

    public static string ChannelClosedMutual(ChannelId channelId, TxId closingTxId) =>
        $"chan:{channelId}:closed:{closingTxId}";

    public static string ChannelForceClosed(ChannelId channelId, TxId commitmentTxId) =>
        $"chan:{channelId}:forceclosed:{commitmentTxId}";

    public static string OutputResolved(TxId txId, uint outputIndex) => $"out:{txId}:{outputIndex}:resolved";

    public static string PenaltyClaimed(TxId revokedCommitmentTxId, uint outputIndex) =>
        $"out:{revokedCommitmentTxId}:{outputIndex}:penalty";

    public static string BreachLoss(TxId revokedCommitmentTxId, uint outputIndex) =>
        $"out:{revokedCommitmentTxId}:{outputIndex}:breach";

    public static string AnchorCpfpFee(TxId childTxId) => $"cpfp:{childTxId}";

    public static string SweepFeeBump(TxId sweepTxId) => $"sweep:{sweepTxId}:fee";

    public static string WalletReceived(TxId txId, uint outputIndex) => $"wallet:{txId}:{outputIndex}:in";

    public static string WalletOutputSpent(TxId txId, uint outputIndex) => $"wallet:{txId}:{outputIndex}:spent";

    public static string WalletSent(TxId txId) => $"wsend:{txId}";

    public static string OpeningBalance(string bucket) => $"open:{bucket}";

    /// <summary>The reversal of <paramref name="originalKey"/> when the block at <paramref name="height"/> was
    /// disconnected.</summary>
    public static string Reversal(string originalKey, uint height) => $"{originalKey}:rev:{height}";

    /// <summary>
    /// The key of the <paramref name="generation"/>th confirmation (2 and up) of the on-chain fact named by
    /// <paramref name="baseKey"/>: a fact whose block a reorg disconnected (its event reversed) is recorded again under a
    /// new key when its transaction confirms again, so the sealer does not take it for a duplicate
    /// (<see cref="Services.AccountingConfirmations.NextConfirmationKey"/>).
    /// </summary>
    public static string Reconfirmed(string baseKey, int generation) => $"{baseKey}:c{generation}";

    /// <summary>An output of a force close that was given up (worth less than its own sweep): its value is lost.
    /// </summary>
    public static string OutputIgnored(TxId txId, uint outputIndex) => $"out:{txId}:{outputIndex}:ignored";

    /// <summary>The backfill's cutover marker (NL-602 A1-T6): the feed starts with opening balances here, written once
    /// per node.</summary>
    public static string Cutover() => "open:cutover";

    /// <summary>The marker the backfill writes once every memo event of the history before the cutover is written.
    /// </summary>
    public static string MemoComplete() => "open:memo:complete";

    /// <summary>The marker the backfill writes once a memo source added after the first memo pass (NL-682) has written
    /// its history: a node whose <see cref="MemoComplete"/> predates the source runs that source alone, once.</summary>
    public static string MemoSourceComplete(string source) => $"open:memo:complete:{source}";
}