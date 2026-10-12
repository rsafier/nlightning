namespace NLightning.Domain.Channels.Splicing.Enums;

/// <summary>
/// Where a channel funding stands (splicing plan §3.3, D6: a pending splice is a sub-state of <c>Open</c>, never a
/// <see cref="Channels.Enums.ChannelState"/>).
/// </summary>
/// <remarks>
/// Persisted by lane SP1-C (<c>ChannelFundings</c>): never renumber. The plan's "Locked" status is not a separate
/// value: a splice locked both ways becomes <see cref="Current"/> in the lock's save and the funding it spent becomes
/// <see cref="Replaced"/>. Revocation data of <see cref="Replaced"/> and <see cref="Discarded"/> fundings is kept until
/// their output is irrevocably spent (SP-I5).
/// </remarks>
public enum ChannelFundingStatus : byte
{
    /// <summary>The funding every commitment is built on today (exactly one per channel).</summary>
    Current = 1,

    /// <summary>A negotiated splice (or RBF attempt) not locked yet: commitments are signed for it too (SP-OP-01).</summary>
    Pending = 2,

    /// <summary>A former current funding, spent by the splice that was locked after it.</summary>
    Replaced = 3,

    /// <summary>A pending funding dropped: an RBF sibling of the locked splice, or a splice whose input was spent by a
    /// commitment of the current funding.</summary>
    Discarded = 4
}