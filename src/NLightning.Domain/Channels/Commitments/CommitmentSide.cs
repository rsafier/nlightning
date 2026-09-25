namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// Which node holds (can broadcast) a commitment transaction.
/// </summary>
public enum CommitmentSide : byte
{
    /// <summary>Our commitment transaction (the peer signs it).</summary>
    Local = 0,

    /// <summary>The peer's commitment transaction (we sign it).</summary>
    Remote = 1,
}