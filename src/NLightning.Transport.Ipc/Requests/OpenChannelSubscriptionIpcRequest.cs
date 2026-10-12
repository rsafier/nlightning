using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for OpenChannelSubscription.
/// </summary>
[MessagePackObject]
public sealed class OpenChannelSubscriptionIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>
    /// The funding transaction the client printed last (NL-535; absent, read as null, from an older client): see
    /// <see cref="OpenChannelClientSubscriptionRequest.KnownFundingTxId"/>.
    /// </summary>
    [Key(1)] public TxId? KnownFundingTxId { get; init; }

    /// <summary>
    /// NL-535: answer on every new published funding transaction (absent, read as false, from an older client, which
    /// keeps the original wait for the next update): see
    /// <see cref="OpenChannelClientSubscriptionRequest.ReportFundingChanges"/>.
    /// </summary>
    [Key(2)] public bool ReportFundingChanges { get; init; }

    public OpenChannelClientSubscriptionRequest ToClientRequest()
    {
        return new OpenChannelClientSubscriptionRequest(ChannelId)
        {
            KnownFundingTxId = KnownFundingTxId,
            ReportFundingChanges = ReportFundingChanges
        };
    }
}