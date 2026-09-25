using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Tlv;
using Interfaces;

public class OpenChannel1MessageHandler : IChannelMessageHandler<OpenChannel1Message>
{
    private readonly IChannelFactory _channelFactory;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<OpenChannel1MessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;

    public OpenChannel1MessageHandler(IChannelFactory channelFactory, IChannelMemoryRepository channelMemoryRepository,
                                      ILogger<OpenChannel1MessageHandler> logger, IMessageFactory messageFactory)
    {
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