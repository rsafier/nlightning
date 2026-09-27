namespace NLightning.Domain.Channels.Commitments;

using Splicing;

/// <summary>
/// One active funding as the update rules see it (splicing plan SP-OP-01, SP-I6): how its main balances differ from
/// the current funding's, and the reserves that apply on its commitments (D9).
/// </summary>
/// <param name="Funding">The funding; null for the current funding of an engine built without funding data.</param>
/// <param name="IsCurrent">True for the current funding.</param>
/// <param name="LocalDeltaMsat">Our main balance on it minus ours on the current funding.</param>
/// <param name="RemoteDeltaMsat">The same for the peer.</param>
/// <param name="LocalReserveMsat">The reserve we must keep on it.</param>
/// <param name="RemoteReserveMsat">The reserve the peer must keep on it.</param>
internal readonly record struct FundingView(
    ChannelFunding? Funding,
    bool IsCurrent,
    long LocalDeltaMsat,
    long RemoteDeltaMsat,
    long LocalReserveMsat,
    long RemoteReserveMsat)
{
    /// <summary>A suffix for rule messages: empty on the current funding, so single-funding texts are unchanged.</summary>
    public string Label => IsCurrent ? string.Empty : $" on splice funding {Funding?.FundingTxId}";
}