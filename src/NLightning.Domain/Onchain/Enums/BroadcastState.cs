namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// The state of a transaction we broadcast (persisted as a byte: never renumber).
/// </summary>
public enum BroadcastState : byte
{
    /// <summary>Not seen in a processed block yet: rebroadcast on every block.</summary>
    Pending = 0,

    /// <summary>Seen in a processed block (it goes back to <see cref="Pending"/> if that block is disconnected).</summary>
    Confirmed = 1,

    /// <summary>Replaced by a later transaction (RBF, <c>ReplacesTxId</c> of the replacement); not rebroadcast.</summary>
    Replaced = 2,

    /// <summary>Given up (dust, or a conflict that can never confirm); not rebroadcast.</summary>
    Abandoned = 3
}