namespace NLightning.Domain.Channels.Interfaces;

using Client.Requests;
using Crypto.ValueObjects;
using Models;
using Money;
using Node.Options;
using Protocol.Messages;
using Signing.Recovery;
using ValueObjects;

/// <summary>Constructs an opening channel from a previously committed public key allocation.</summary>
public interface IAllocatedV1ChannelFactory : IChannelFactory
{
    Task<ChannelModel> CreateAllocatedV1AsNonInitiatorAsync(OpenChannel1Message message,
        FeatureOptions negotiatedFeatures, CompactPubKey remoteNodeId, ChannelKeyAllocation allocation,
        V1OpeningContext? context = null);
    Task<ChannelModel> CreateAllocatedV1AsInitiatorAsync(OpenChannelClientRequest request,
        FeatureOptions negotiatedFeatures, CompactPubKey remoteNodeId, ChannelId temporaryId,
        ChannelKeyAllocation allocation, V1OpeningContext? context = null);
    Task<V1OpeningPreparation> PrepareV1AsInitiatorAsync(OpenChannelClientRequest request,
        FeatureOptions negotiatedFeatures, V1OpeningContext context);
    Task<V1OpeningPreparation> PrepareV1AsNonInitiatorAsync(OpenChannel1Message message,
        FeatureOptions negotiatedFeatures, V1OpeningContext context);
}

public sealed record V1OpeningContext(NodeOptions Options, LightningMoney FeeQuote);
public sealed record V1OpeningPreparation(ChannelParams Parameters, LightningMoney LocalBalance,
    LightningMoney RemoteBalance, ChannelKeySetModel? RemoteKeys);