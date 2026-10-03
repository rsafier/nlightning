using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet.Models;

/// <summary>What bitcoind did with a package (<c>submitpackage</c>, BOLT 5 plan O7-T2, NL-380).</summary>
public enum PackageSubmitStatus
{
    /// <summary>Every transaction of the package is in bitcoind's mempool (or was already).</summary>
    Accepted,

    /// <summary>bitcoind evaluated the package and refused at least one of its transactions.</summary>
    Rejected,

    /// <summary>
    /// bitcoind has no usable <c>submitpackage</c> (an older node, or one restricted to regtest): the transactions
    /// can only be sent one by one.
    /// </summary>
    Unsupported,

    /// <summary>The call failed before bitcoind evaluated the package (connection, RPC error); retry later.</summary>
    Failed
}

/// <summary>One transaction's result in a <c>submitpackage</c> answer.</summary>
/// <param name="TxId">The transaction's id.</param>
/// <param name="Accepted">True when it is in the mempool (added now or already there).</param>
/// <param name="Error">bitcoind's reason when it was refused (<c>unevaluated</c> when it was not looked at).</param>
/// <param name="EffectiveFeerateBtcPerKvb">The feerate it was evaluated at (its package's), when reported.</param>
public sealed record PackageTransactionResult(uint256 TxId, bool Accepted, string? Error,
                                              decimal? EffectiveFeerateBtcPerKvb)
{
    /// <summary>True when it was refused because it (or its package) pays too little.</summary>
    public bool IsFeeRefusal => PackageSubmitResult.IsFeeReason(Error);
}

/// <summary>
/// The answer to <c>submitpackage</c> for one parent and its child (Bitcoin Core 28+ 1p1c package relay, NL-380): the
/// overall status, bitcoind's package message and the result of each transaction.
/// </summary>
/// <param name="Status">What bitcoind did with the package.</param>
/// <param name="Message">bitcoind's <c>package_msg</c> (Core 28+; <c>success</c> when accepted), or the reason of an
/// unsupported or failed call.</param>
/// <param name="Transactions">The result of each transaction (empty when the package was not evaluated).</param>
/// <param name="PackageFeerateBtcPerKvb">The <c>package-feerate</c> an older node (Core 26/27) reports; else the
/// highest <c>effective-feerate</c> of the transactions; null when none is reported.</param>
public sealed record PackageSubmitResult(PackageSubmitStatus Status, string? Message,
                                         IReadOnlyList<PackageTransactionResult> Transactions,
                                         decimal? PackageFeerateBtcPerKvb = null)
{
    // bitcoind reasons for a transaction or package paying too little (validation.cpp, policy/packages.cpp)
    private static readonly string[] s_feeReasons =
    [
        "min relay fee not met", "mempool min fee not met", "fee-too-low", "insufficient fee"
    ];

    /// <summary>True when the package, or one of its transactions, was refused for its fee.</summary>
    public bool IsFeeRefusal => Status == PackageSubmitStatus.Rejected
                             && (IsFeeReason(Message) || Transactions.Any(t => t.IsFeeRefusal));

    /// <summary>A package bitcoind cannot take (no usable <c>submitpackage</c>).</summary>
    public static PackageSubmitResult Unsupported(string reason) => new(PackageSubmitStatus.Unsupported, reason, []);

    /// <summary>A call that failed before bitcoind evaluated the package.</summary>
    public static PackageSubmitResult Failed(string reason) => new(PackageSubmitStatus.Failed, reason, []);

    /// <summary>The first refusal reason (package message or transaction error), for logs.</summary>
    public string Describe() =>
        Transactions.Count == 0
            ? Message ?? Status.ToString()
            : $"{Message ?? Status.ToString()}: "
            + string.Join("; ", Transactions.Select(t => $"{t.TxId} {(t.Accepted ? "accepted" : t.Error)}"));

    internal static bool IsFeeReason(string? reason) =>
        reason is not null && s_feeReasons.Any(r => reason.Contains(r, StringComparison.OrdinalIgnoreCase));
}