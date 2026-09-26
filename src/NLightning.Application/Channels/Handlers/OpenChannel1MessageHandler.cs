using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Handlers;

using Domain.Bitcoin.Constants;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

public class OpenChannel1MessageHandler : IChannelMessageHandler<OpenChannel1Message>
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelFactory _channelFactory;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<OpenChannel1MessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly GossipOptions _gossipOptions;

    /// <param name="gossipOptions">Whether public channels are accepted (<see cref="GossipOptions.AcceptPublicChannels"/>,
    /// default yes).</param>
    public OpenChannel1MessageHandler(IChannelFactory channelFactory, IChannelMemoryRepository channelMemoryRepository,
                                      ILogger<OpenChannel1MessageHandler> logger, IMessageFactory messageFactory,
                                      IBlockchainMonitor? blockchainMonitor = null,
                                      IOptions<GossipOptions>? gossipOptions = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _channelFactory = channelFactory;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _messageFactory = messageFactory;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(
        OpenChannel1Message message, ChannelState currentState, FeatureOptions negotiatedFeatures,
        CompactPubKey peerPubKey)
    {
        _logger.LogTrace("Processing OpenChannel1Message with ChannelId: {ChannelId} from Peer: {PeerPubKey}",
                         message.Payload.ChannelId, peerPubKey);

        var payload = message.Payload;

        if (currentState != ChannelState.None)
            throw new ChannelErrorException("A channel with this id already exists", payload.ChannelId);

        // NL-216: a node that does not follow the chain could not see the funding confirm or be cheated on it
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            throw new ChannelErrorException(ChainProcessingHalt.Refusal("open_channel"), payload.ChannelId,
                                            "Not accepting channels right now, try again later");

        // BOLT 2: the receiver MAY fail a channel whose announce_channel it does not want (NL-341). The flag is stored
        // with the channel by the factory
        if (payload.ChannelFlags.AnnounceChannel && !_gossipOptions.AcceptPublicChannels)
            throw new ChannelErrorException("Refusing a public channel: Gossip:AcceptPublicChannels is false",
                                            payload.ChannelId, "We don't accept public channels");

        // Check if there's a temporary channel for this peer
        if (_channelMemoryRepository.TryGetTemporaryChannelState(peerPubKey, payload.ChannelId, out currentState))
        {
            if (currentState != ChannelState.V1Opening)
            {
                throw new ChannelErrorException("Channel had the wrong state", payload.ChannelId,
                                                "This channel is already being negotiated with peer");
            }
        }

        // Create the channel
        var channel = await _channelFactory.CreateChannelV1AsNonInitiatorAsync(message, negotiatedFeatures, peerPubKey);

        _logger.LogTrace("Created Channel with fundingPubKey: {fundingPubKey}",
                         channel.LocalKeySet.FundingCompactPubKey);

        // Add the channel to dictionaries
        _channelMemoryRepository.AddTemporaryChannel(peerPubKey, channel);

        // Create UpfrontShutdownScriptTlv if needed
        UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv = null;
        if (channel.LocalUpfrontShutdownScript is not null)
            upfrontShutdownScriptTlv = new UpfrontShutdownScriptTlv(channel.LocalUpfrontShutdownScript.Value);
        else
            upfrontShutdownScriptTlv = new UpfrontShutdownScriptTlv(Array.Empty<byte>());

        // BOLT 2: accept_channel MUST carry the channel_type from open_channel (NL-218). The factory's validator has
        // already refused a missing or unsupported type, so echo the opener's bytes as they are.
        var channelTypeTlv = message.ChannelTypeTlv
                          ?? throw new ChannelErrorException("Channel type was not provided", payload.ChannelId);

        // Create the reply message with the values we announce, never the opener's (NL-194)
        var acceptChannel1ReplyMessage = _messageFactory
           .CreateAcceptChannel1Message(channel.ChannelParams.Local, channelTypeTlv,
                                        channel.LocalKeySet.DelayedPaymentCompactBasepoint,
                                        channel.LocalKeySet.CurrentPerCommitmentCompactPoint,
                                        channel.LocalKeySet.FundingCompactPubKey,
                                        channel.LocalKeySet.HtlcCompactBasepoint, channel.ChannelParams.MinimumDepth,
                                        channel.LocalKeySet.PaymentCompactBasepoint,
                                        channel.LocalKeySet.RevocationCompactBasepoint, channel.ChannelId,
                                        upfrontShutdownScriptTlv);

        return [acceptChannel1ReplyMessage];
    }
}