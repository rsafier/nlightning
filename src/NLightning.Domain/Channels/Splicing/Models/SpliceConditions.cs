namespace NLightning.Domain.Channels.Splicing.Models;

/// <summary>
/// The channel facts the splice rules (<see cref="SpliceRules"/>) judge, gathered under the channel's lock.
/// </summary>
/// <param name="IsNegotiated">Both 35 and 63 negotiated (D14).</param>
/// <param name="IsQuiescent">The channel is quiescent (SP-S-01, SP-R-01).</param>
/// <param name="LocalIsQuiescenceInitiator">We are the quiescence initiator (only the initiator may send
/// <c>splice_init</c>).</param>
/// <param name="ChannelReadyExchanged"><c>channel_ready</c> sent and received.</param>
/// <param name="SpliceNegotiating">A splice is being negotiated.</param>
/// <param name="HasUnlockedSplice">A negotiated splice is not locked both ways yet.</param>
/// <param name="ShutdownSent">We sent <c>shutdown</c>.</param>
/// <param name="ShutdownReceived">The peer sent <c>shutdown</c>.</param>
/// <param name="LocalBalanceMsat">Our current main balance (for a splice-out of ours).</param>
/// <param name="RemoteBalanceMsat">The peer's current main balance (for a splice-out of its).</param>
public sealed record SpliceConditions(
    bool IsNegotiated,
    bool IsQuiescent,
    bool LocalIsQuiescenceInitiator,
    bool ChannelReadyExchanged,
    bool SpliceNegotiating,
    bool HasUnlockedSplice,
    bool ShutdownSent,
    bool ShutdownReceived,
    ulong LocalBalanceMsat,
    ulong RemoteBalanceMsat);