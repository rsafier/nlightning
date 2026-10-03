using NBitcoin.RPC;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Onchain.Enums;

/// <summary>
/// The abandonment rule for a stored broadcast bitcoind refuses for good (NL-294): which refusals are permanent, and
/// which transactions the chain monitor may give up on its own.
/// </summary>
/// <remarks>
/// <para>A refusal is permanent when resending the same bytes can never succeed: the inputs are missing or already
/// spent (<c>bad-txns-inputs-missingorspent</c>, <c>missing-inputs</c>), or the transaction is invalid by consensus
/// or by script (<c>bad-txns-*</c> other than the premature coinbase spend, <c>mandatory-script-verify-flag-failed</c>,
/// <c>non-mandatory-script-verify-flag</c>). Everything else (fees, <c>non-final</c>, <c>non-BIP68-final</c>, mempool
/// limits or conflicts, a node that is down) is temporary and resets the count.</para>
/// <para>The chain monitor abandons only a transaction that spends wallet outputs alone: a channel funding
/// (<see cref="BroadcastPurpose.Funding"/>, and <see cref="BroadcastPurpose.Unspecified"/>, the legacy funding path)
/// and a <see cref="BroadcastPurpose.WalletSend"/>, plus our anchor sweep (<see cref="BroadcastPurpose.AnchorSweep"/>,
/// NL-611: anyone may take anchors, so its inputs are often spent by someone else). Nothing about a channel's safety
/// depends on those; abandoning one releases its wallet inputs. A commitment, penalty, HTLC transaction, sweep, claim, CPFP child or mutual close spends
/// a channel output: it is never abandoned for refusals, however many, because an input that is merely not confirmed
/// yet (a parent still in flight, evicted or reorged out) is refused as missing too, and giving up would lose funds.
/// Those are given up only by the explicit rules of the component that proves a conflicting spend on chain (the
/// on-chain watcher for our commitment, the sweep scheduler for sweeps, claims and penalties); the monitor keeps
/// sending them and logs an error once they reach the refusal threshold.</para>
/// </remarks>
internal static class BroadcastRefusalRules
{
    private static readonly string[] s_missingInputs = ["bad-txns-inputs-missingorspent", "missing-inputs",
                                                        "missing inputs"];

    // bitcoind's answers for a transaction whose outputs are already in its UTXO set: a confirmed transaction
    private static readonly string[] s_inChain = ["already in block chain", "already in utxo set", "txn-already-known"];

    private static readonly string[] s_invalid = ["mandatory-script-verify-flag-failed",
                                                  "non-mandatory-script-verify-flag"];

    private static readonly string[] s_temporaryBadTxns = ["bad-txns-premature-spend-of-coinbase",
                                                           "bad-txns-nonfinal"];

    /// <summary>True when bitcoind refused the transaction for a reason that sending it again cannot change.</summary>
    public static bool IsPermanent(Exception sendError)
    {
        ArgumentNullException.ThrowIfNull(sendError);
        if (sendError is RPCException { RPCCode: RPCErrorCode.RPC_DESERIALIZATION_ERROR })
            return true;

        var message = sendError.Message;
        if (IsMissingInputs(sendError) || s_invalid.Any(r => Contains(message, r)))
            return true;

        return Contains(message, "bad-txns-") && !s_temporaryBadTxns.Any(r => Contains(message, r));
    }

    /// <summary>
    /// True when bitcoind answered <c>sendrawtransaction</c> with a refusal of the transaction itself (NL-534): a
    /// verification failure (<c>RPC_VERIFY_ERROR</c>, e.g. <c>bad-txns-inputs-missingorspent</c>), a policy rejection
    /// (<c>RPC_VERIFY_REJECTED</c>: a mempool conflict, a fee too low, <c>txn-already-known</c>) or a transaction already
    /// in the chain (<c>RPC_VERIFY_ALREADY_IN_CHAIN</c>). Those are routine for a stored broadcast the chain monitor
    /// sends again (an RBF sibling that lost, a transaction a block already holds), so the chain service leaves their
    /// log level to its caller; anything else (an unreachable node, bad bytes, an RPC error of another kind) is not a
    /// refusal.
    /// </summary>
    public static bool IsNodeRefusal(Exception sendError)
    {
        ArgumentNullException.ThrowIfNull(sendError);
        return sendError is RPCException
        {
            RPCCode: RPCErrorCode.RPC_VERIFY_ERROR or RPCErrorCode.RPC_VERIFY_REJECTED
                  or RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN
        };
    }

    /// <summary>True when bitcoind refused the transaction because an input is missing or already spent.</summary>
    public static bool IsMissingInputs(Exception sendError)
    {
        ArgumentNullException.ThrowIfNull(sendError);
        return s_missingInputs.Any(r => Contains(sendError.Message, r));
    }

    /// <summary>
    /// True when the refusal may mean that the transaction is already confirmed (NL-779): its outputs are in bitcoind's
    /// UTXO set (<c>RPC_VERIFY_ALREADY_IN_CHAIN</c>, "Transaction outputs already in utxo set", the older "already in
    /// block chain", <c>txn-already-known</c>), or its inputs are missing or spent, which is also what a confirmed
    /// transaction whose outputs are all spent gets. The chain monitor then asks where it confirmed before it counts
    /// the refusal.
    /// </summary>
    public static bool MayBeConfirmed(Exception sendError)
    {
        ArgumentNullException.ThrowIfNull(sendError);
        if (sendError is RPCException { RPCCode: RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN })
            return true;

        return IsMissingInputs(sendError) || s_inChain.Any(r => Contains(sendError.Message, r));
    }

    /// <summary>
    /// True when the chain monitor may abandon a transaction of this purpose after permanent refusals: it spends wallet
    /// outputs only.
    /// </summary>
    /// <remarks>A <see cref="BroadcastPurpose.Splice"/> is treated as a funding, as it was while splices were saved as
    /// <see cref="BroadcastPurpose.Funding"/> (NL-626 changed only the label).</remarks>
    public static bool MayAbandon(BroadcastPurpose purpose) =>
        IsFunding(purpose) || purpose is BroadcastPurpose.WalletSend or BroadcastPurpose.AnchorSweep;

    /// <summary>
    /// True when abandoning a transaction of this purpose releases its channel's wallet UTXO locks: a funding, the legacy
    /// funding path (<see cref="BroadcastPurpose.Unspecified"/>) and a splice (NL-626: same rule as before the splice
    /// got a purpose of its own).
    /// </summary>
    public static bool IsFunding(BroadcastPurpose purpose) =>
        purpose is BroadcastPurpose.Funding or BroadcastPurpose.Unspecified or BroadcastPurpose.Splice;

    private static bool Contains(string message, string reason) =>
        message.Contains(reason, StringComparison.OrdinalIgnoreCase);
}