namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// Where one output of a commitment on chain stands (BOLT 5 §General Nomenclature). Persisted later as a byte; never
/// renumber.
/// </summary>
public enum OutputResolutionState : byte
{
    /// <summary>Still to be resolved: waiting for a timelock or a preimage, or our resolving transaction is not
    /// confirmed yet.</summary>
    Unresolved = 1,

    /// <summary>Resolved by a confirmed transaction (or by the commitment itself), less than 100 blocks deep.</summary>
    Resolved = 2,

    /// <summary>Irrevocably resolved: the resolving transaction is at least 100 blocks deep, or an HTLC that is not ours
    /// to claim has expired (B5-GEN-02, B5-LCL-RO-04, B5-RMT-RO-02).</summary>
    IrrevocablyResolved = 3
}