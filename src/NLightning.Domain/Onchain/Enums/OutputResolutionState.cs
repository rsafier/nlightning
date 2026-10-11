namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// Where the resolution of one output stands (persisted as a byte in <c>OutputResolutions.State</c>: never renumber).
/// </summary>
public enum OutputResolutionState : byte
{
    /// <summary>Known, nothing decided yet.</summary>
    Pending = 0,

    /// <summary>Waiting for a height (<c>WaitUntilHeight</c>: a CSV delay or a CLTV expiry) or a preimage.</summary>
    Waiting = 1,

    /// <summary>Our resolving transaction (<c>ResolvingTxId</c>) was saved and broadcast; not in a block yet.</summary>
    Broadcast = 2,

    /// <summary>A transaction spending the output is in the active chain (<c>ResolvedHeight</c>), not 100 deep yet.</summary>
    Resolved = 3,

    /// <summary>The spend is irrevocably resolved (100 blocks deep, BOLT 5).</summary>
    Irrevocable = 4,

    /// <summary>Nothing to do or nothing possible (theirs, dust, unrecoverable).</summary>
    Ignored = 5
}