namespace NLightning.Domain.Client.Requests;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;

public class OpenChannelClientSubscriptionRequest
{
    public ChannelId ChannelId { get; }

    /// <summary>
    /// NL-535: answer as soon as the channel, still waiting for its funding, runs on a published funding transaction
    /// other than <see cref="KnownFundingTxId"/> (a dual-funded open's first attempt, an RBF attempt of either side, or
    /// the earlier attempt that confirmed), at once when that is already the case, and otherwise when the channel is
    /// ready. False (an older client) keeps the original behaviour: wait for the channel's next update.
    /// </summary>
    public bool ReportFundingChanges { get; init; }

    /// <summary>
    /// The funding transaction the client printed last, or null when it printed none (see
    /// <see cref="ReportFundingChanges"/>).
    /// </summary>
    public TxId? KnownFundingTxId { get; init; }

    public OpenChannelClientSubscriptionRequest(ChannelId channelId)
    {
        ChannelId = channelId;
    }
}