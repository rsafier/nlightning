namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// What <see cref="Planners.OutputResolutionPlanner"/> asks the watcher to do for one output (BOLT 5 plan §3.1).
/// </summary>
public enum ResolutionActionKind : byte
{
    /// <summary>Nothing to do before the tip reaches <see cref="Models.ResolutionAction.WaitUntilHeight"/>.</summary>
    Wait = 1,

    /// <summary>Broadcast our pre-signed HTLC-timeout transaction for the output (B5-LCL-LO-02).</summary>
    BroadcastHtlcTimeoutTx = 2,

    /// <summary>Broadcast our HTLC-success transaction with <see cref="Models.ResolutionAction.Preimage"/>
    /// (B5-LCL-RO-01).</summary>
    BroadcastHtlcSuccessTx = 3,

    /// <summary>Broadcast a sweep, claim or penalty spending the output (or its second-level output) the way
    /// <see cref="Models.ResolutionAction.SpendKind"/> says.</summary>
    Sweep = 4,

    /// <summary>Raise <c>OutgoingHtlcFulfilled</c> for our offered HTLC with the preimage (persist it first,
    /// BOLT2 I10).</summary>
    RaiseFulfilled = 5,

    /// <summary>Raise <c>OutgoingHtlcFailed</c> (on-chain timeout, no reason bytes; O3-T4) for our offered HTLC.</summary>
    RaiseFailed = 6,

    /// <summary>Warn the user: funds of this output went to the peer by a path it should not have had
    /// (B5-GEN-06).</summary>
    AlertLostFunds = 7
}