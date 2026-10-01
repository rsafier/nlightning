using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Handlers;

using Close;
using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Constants;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

public class OpenChannel1MessageHandler : IChannelMessageHandler<OpenChannel1Message>
{
    private readonly IAnchorReserveService? _anchorReserveService;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelFactory _channelFactory;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<OpenChannel1MessageHandler> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly INodeDrainState? _nodeDrainState;
    private readonly GossipOptions _gossipOptions;
    private readonly NodeOptions _nodeOptions;
    private readonly UpfrontShutdownScriptSource? _upfrontShutdownScriptSource;

    /// <param name="gossipOptions">Whether public channels are accepted (<see cref="GossipOptions.AcceptPublicChannels"/>,
    /// default yes, and on mainnet only with <see cref="GossipOptions.AllowPublicChannelsOnMainnet"/>).</param>
    /// <param name="nodeOptions">The node's network for the mainnet gate (plan D12); without it the handler assumes
    /// mainnet, the safe default.</param>
    /// <param name="anchorReserveService">Refuses an <c>option_anchors</c> channel the wallet could not back with its
    /// anchors reserve (NL-379); registered by <c>AddBitcoinInfrastructure</c>.</param>
    /// <param name="upfrontShutdownScriptSource">Our <c>upfront_shutdown_script</c> when
    /// <c>option_upfront_shutdown_script</c> is negotiated (NL-045); without it a zero-length script is sent.</param>
    public OpenChannel1MessageHandler(IChannelFactory channelFactory, IChannelMemoryRepository channelMemoryRepository,
                                      ILogger<OpenChannel1MessageHandler> logger, IMessageFactory messageFactory,
                                      IBlockchainMonitor? blockchainMonitor = null,
                                      IOptions<GossipOptions>? gossipOptions = null,
                                      IOptions<NodeOptions>? nodeOptions = null,
                                      IAnchorReserveService? anchorReserveService = null,
                                      UpfrontShutdownScriptSource? upfrontShutdownScriptSource = null,
                                      INodeDrainState? nodeDrainState = null)
    {
        _nodeDrainState = nodeDrainState;
        _upfrontShutdownScriptSource = upfrontShutdownScriptSource;
        _anchorReserveService = anchorReserveService;
        _blockchainMonitor = blockchainMonitor;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _nodeOptions = nodeOptions?.Value ?? new NodeOptions();
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

        // NL-591: a node draining for its shutdown opens nothing new
        if (_nodeDrainState is { IsDraining: true })
            throw new ChannelErrorException(NodeDrain.Refusal("open_channel"), payload.ChannelId,
                                            "Not accepting channels right now, try again later");

        // BOLT 2: the receiver MAY fail a channel whose announce_channel it does not want (NL-341). The flag is stored
        // with the channel by the factory
        if (payload.ChannelFlags.AnnounceChannel && !_gossipOptions.AcceptPublicChannels)
            throw new ChannelErrorException("Refusing a public channel: Gossip:AcceptPublicChannels is false",
                                            payload.ChannelId, "We don't accept public channels");

        // BOLT 7: the fundee of a public channel MUST send announcement_signatures at depth. Where we would never send
        // them (mainnet without Gossip:AllowPublicChannelsOnMainnet, plan D12) we refuse the channel instead of
        // accepting it and leaving the opener waiting for our half forever
        if (payload.ChannelFlags.AnnounceChannel
         && !_gossipOptions.ArePublicChannelsAllowed(_nodeOptions.BitcoinNetwork))
            throw new ChannelErrorException(
                "Refusing a public channel on mainnet: Gossip:AllowPublicChannelsOnMainnet is false",
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

        // NL-379: as fundee of an anchors channel we still pay the CPFP child of our commitment and the fee inputs of
        // our HTLC transactions from the wallet, so refuse the channel when the confirmed balance can't keep the anchors
        // reserve with it (LND does the same). The channel type decided the anchors, as it does in the factory. Once
        // admitted, the channel counts toward the reserve while it is being opened, so concurrent opens can't all pass
        // against the reserve of one
        if (channel.ChannelParams.OptionAnchorOutputs && _anchorReserveService is not null)
        {
            try
            {
                await _anchorReserveService.EnsureCanAcceptAnchorsChannelAsync(channel);
            }
            catch (AnchorReserveException e)
            {
                throw new ChannelErrorException(e.Message, payload.ChannelId,
                                                "Not enough on-chain funds to keep the anchors reserve for this "
                                              + "channel");
            }
        }

        // Add the channel to dictionaries
        _channelMemoryRepository.AddTemporaryChannel(peerPubKey, channel);

        // NL-045: once the channel is admitted, a reserved wallet address becomes our upfront shutdown script. After the
        // temporary channel is stored, so the source can tell a live open from an abandoned one whose script it may
        // hand out again
        if (_upfrontShutdownScriptSource is not null)
        {
            try
            {
                await _upfrontShutdownScriptSource.AssignIfNegotiatedAsync(channel, negotiatedFeatures, peerPubKey);
            }
            catch
            {
                _channelMemoryRepository.TryRemoveTemporaryChannel(peerPubKey, channel.ChannelId);
                throw;
            }
        }

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