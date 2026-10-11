namespace NLightning.Domain.Bitcoin.Transactions.Enums;

/// <summary>
/// The BOLT 3 second-stage HTLC transactions that spend an HTLC output of a commitment transaction.
/// </summary>
public enum HtlcTransactionType : byte
{
    /// <summary>Spends an HTLC the commitment holder offered, after <c>cltv_expiry</c> (locktime = cltv_expiry).</summary>
    Timeout = 1,

    /// <summary>Spends an HTLC the commitment holder received, with the payment preimage (locktime 0).</summary>
    Success = 2
}