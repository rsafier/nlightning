namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// What spent a channel's funding output (persisted as a byte in <c>ChannelCloses.Kind</c>: never renumber). The
/// classifier (BOLT 5 plan O2-T3) picks one per funding spend.
/// </summary>
public enum ChannelCloseKind : byte
{
    /// <summary>A mutual close transaction (BOLT 5 §Mutual Close Handling).</summary>
    Mutual = 1,

    /// <summary>Our latest commitment transaction (§Local Commitment Transaction).</summary>
    LocalCommitment = 2,

    /// <summary>The peer's current commitment transaction (§Remote Commitment Transaction).</summary>
    RemoteCommitment = 3,

    /// <summary>The peer's next commitment: we signed it and the peer has not revoked the current one yet.</summary>
    RemoteNextCommitment = 4,

    /// <summary>A peer commitment it already revoked (§Revoked Transaction Close Handling).</summary>
    RevokedCommitment = 5,

    /// <summary>A peer commitment newer than any we know (we lost data): only <c>to_remote</c> can be swept.</summary>
    FutureCommitment = 6,

    /// <summary>A spend we cannot classify (CRITICAL; nothing to sweep).</summary>
    Unknown = 7
}