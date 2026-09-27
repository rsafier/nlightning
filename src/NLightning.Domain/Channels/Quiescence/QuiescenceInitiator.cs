namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// Which side is the quiescence initiator once the channel is quiescent (BOLT 2 "Channel Quiescence").
/// </summary>
/// <remarks>
/// The initiator is the side that sent <c>stfu</c> with <c>initiator</c> = 1 first. When both sent
/// <c>initiator</c> = 1 the channel funder (the sender of <c>open_channel</c>) is the initiator (Q-R-05). Only the
/// initiator may start the dependent protocol (for example send <c>splice_init</c>).
/// </remarks>
public enum QuiescenceInitiator : byte
{
    /// <summary>We are the initiator.</summary>
    Local = 1,

    /// <summary>The peer is the initiator.</summary>
    Remote = 2
}