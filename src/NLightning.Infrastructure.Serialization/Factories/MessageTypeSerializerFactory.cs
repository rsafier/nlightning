using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Factories;

using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Interfaces;
using Messages.Types;

public class MessageTypeSerializerFactory : IMessageTypeSerializerFactory
{
    private readonly Dictionary<Type, IMessageTypeSerializer> _serializers = new();
    private readonly Dictionary<MessageTypes, Type> _messageTypeDictionary = new();
    private readonly IPayloadSerializerFactory _payloadSerializerFactory;
    private readonly ITlvConverterFactory _tlvConverterFactory;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;
    private readonly Wire.WireRegistry _wireRegistry;

    public MessageTypeSerializerFactory(IPayloadSerializerFactory payloadSerializerFactory,
                                        ITlvConverterFactory tlvConverterFactory,
                                        ITlvStreamSerializer tlvStreamSerializer)
    {
        _payloadSerializerFactory = payloadSerializerFactory;
        _tlvConverterFactory = tlvConverterFactory;
        _tlvStreamSerializer = tlvStreamSerializer;
        _wireRegistry = new Wire.WireRegistry(tlvConverterFactory);

        RegisterSerializers();
        RegisterTypeDictionary();
    }

    public IMessageTypeSerializer<TMessageType>? GetSerializer<TMessageType>() where TMessageType : IMessage
    {
        // Migrated messages are wire definitions; the hand-written serializers serve the rest
        if (_wireRegistry.Get<TMessageType>() is { } wire)
            return wire as IMessageTypeSerializer<TMessageType>;

        return _serializers.GetValueOrDefault(typeof(TMessageType)) as IMessageTypeSerializer<TMessageType>;
    }

    public IMessageTypeSerializer? GetSerializer(MessageTypes messageType)
    {
        if (_wireRegistry.Get(messageType) is { } wire)
            return wire;

        var type = _messageTypeDictionary.GetValueOrDefault(messageType);
        if (type is null)
            return null;

        return _serializers.GetValueOrDefault(type);
    }

    private void RegisterSerializers()
    {
        _serializers.Add(typeof(AcceptChannel1Message),
                         new AcceptChannel1MessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                                 _tlvStreamSerializer));
        _serializers.Add(typeof(AcceptChannel2Message),
                         new AcceptChannel2MessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                                 _tlvStreamSerializer));
        _serializers.Add(typeof(ChannelReadyMessage),
                         new ChannelReadyMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                               _tlvStreamSerializer));
        _serializers.Add(typeof(ClosingSignedMessage),
                 new ClosingSignedMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                        _tlvStreamSerializer));
        _serializers.Add(typeof(ClosingCompleteMessage),
                         new ClosingCompleteMessageTypeSerializer(_payloadSerializerFactory, _tlvStreamSerializer));
        _serializers.Add(typeof(ClosingSigMessage),
                         new ClosingSigMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                             _tlvStreamSerializer));
        _serializers.Add(typeof(FundingCreatedMessage),
                         new FundingCreatedMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                                  _tlvStreamSerializer));
        _serializers.Add(typeof(FundingSignedMessage),
                         new FundingSignedMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                                 _tlvStreamSerializer));
        _serializers.Add(typeof(OpenChannel1Message),
                         new OpenChannel1MessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                               _tlvStreamSerializer));
        _serializers.Add(typeof(OpenChannel2Message),
                         new OpenChannel2MessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                               _tlvStreamSerializer));
        _serializers.Add(typeof(OnionMessageMessage),
                         new OnionMessageMessageTypeSerializer(_payloadSerializerFactory, _tlvStreamSerializer));
        _serializers.Add(typeof(PeerStorageMessage),
         new PeerStorageMessageTypeSerializer(_payloadSerializerFactory, _tlvStreamSerializer));
        _serializers.Add(typeof(PeerStorageRetrievalMessage),
                         new PeerStorageRetrievalMessageTypeSerializer(_payloadSerializerFactory,
                                                                       _tlvStreamSerializer));
        _serializers.Add(typeof(ShutdownMessage),
                 new ShutdownMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                   _tlvStreamSerializer));
        _serializers.Add(typeof(SpliceAckMessage),
                         new SpliceAckMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                            _tlvStreamSerializer));
        _serializers.Add(typeof(SpliceInitMessage),
                         new SpliceInitMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                             _tlvStreamSerializer));
        _serializers.Add(typeof(SpliceLockedMessage),
                         new SpliceLockedMessageTypeSerializer(_payloadSerializerFactory, _tlvStreamSerializer));
        _serializers.Add(typeof(StartBatchMessage),
                         new StartBatchMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                             _tlvStreamSerializer));
        _serializers.Add(typeof(StfuMessage), new StfuMessageTypeSerializer(_payloadSerializerFactory));
        _serializers.Add(typeof(TxAbortMessage), new TxAbortMessageTypeSerializer(_payloadSerializerFactory));
        _serializers.Add(typeof(TxAckRbfMessage),
                         new TxAckRbfMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                           _tlvStreamSerializer));
        _serializers.Add(typeof(TxAddOutputMessage),
                 new TxAddOutputMessageTypeSerializer(_payloadSerializerFactory));
        _serializers.Add(typeof(TxCompleteMessage),
                         new TxCompleteMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                             _tlvStreamSerializer));
        _serializers.Add(typeof(TxInitRbfMessage),
                         new TxInitRbfMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                            _tlvStreamSerializer));
        _serializers.Add(typeof(TxRemoveInputMessage),
                         new TxRemoveInputMessageTypeSerializer(_payloadSerializerFactory));
        _serializers.Add(typeof(TxRemoveOutputMessage),
                         new TxRemoveOutputMessageTypeSerializer(_payloadSerializerFactory));
        _serializers.Add(typeof(TxSignaturesMessage),
                         new TxSignaturesMessageTypeSerializer(_payloadSerializerFactory, _tlvConverterFactory,
                                                               _tlvStreamSerializer));

        // BOLT 7 gossip queries are parsed so the node can answer them
        _serializers.Add(typeof(QueryShortChannelIdsMessage),
                         new QueryShortChannelIdsMessageTypeSerializer(_payloadSerializerFactory,
                                                                       _tlvStreamSerializer));
        _serializers.Add(typeof(ReplyShortChannelIdsEndMessage),
                         new ReplyShortChannelIdsEndMessageTypeSerializer(_payloadSerializerFactory));
        _serializers.Add(typeof(QueryChannelRangeMessage),
                         new QueryChannelRangeMessageTypeSerializer(_payloadSerializerFactory, _tlvStreamSerializer));
        _serializers.Add(typeof(ReplyChannelRangeMessage),
                         new ReplyChannelRangeMessageTypeSerializer(_payloadSerializerFactory, _tlvStreamSerializer));
        _serializers.Add(typeof(GossipTimestampFilterMessage),
                         new GossipTimestampFilterMessageTypeSerializer(_payloadSerializerFactory));

        // channel_update is parsed: it is exchanged directly with channel peers and embedded in BOLT 4 UPDATE failures
        _serializers.Add(typeof(ChannelUpdateMessage),
                         new ChannelUpdateMessageTypeSerializer(_payloadSerializerFactory));

        // BOLT 7 announcements: the Domain payload is the codec (plan D1); announcement_signatures is a channel message
        _serializers.Add(typeof(ChannelAnnouncementMessage),
                         new GossipAnnouncementMessageTypeSerializer<ChannelAnnouncementMessage,
                             ChannelAnnouncementPayload>(_payloadSerializerFactory,
                                                         p => new ChannelAnnouncementMessage(p)));
        _serializers.Add(typeof(NodeAnnouncementMessage),
                         new GossipAnnouncementMessageTypeSerializer<NodeAnnouncementMessage, NodeAnnouncementPayload>(
                             _payloadSerializerFactory, p => new NodeAnnouncementMessage(p)));
        _serializers.Add(typeof(AnnouncementSignaturesMessage),
                         new AnnouncementSignaturesMessageTypeSerializer(_payloadSerializerFactory,
                                                                         _tlvStreamSerializer));
    }

    private void RegisterTypeDictionary()
    {
        _messageTypeDictionary.Add(MessageTypes.AcceptChannel, typeof(AcceptChannel1Message));
        _messageTypeDictionary.Add(MessageTypes.AcceptChannel2, typeof(AcceptChannel2Message));
        _messageTypeDictionary.Add(MessageTypes.ChannelReady, typeof(ChannelReadyMessage));
        _messageTypeDictionary.Add(MessageTypes.ClosingSigned, typeof(ClosingSignedMessage));
        _messageTypeDictionary.Add(MessageTypes.ClosingComplete, typeof(ClosingCompleteMessage));
        _messageTypeDictionary.Add(MessageTypes.ClosingSig, typeof(ClosingSigMessage));
        _messageTypeDictionary.Add(MessageTypes.FundingCreated, typeof(FundingCreatedMessage));
        _messageTypeDictionary.Add(MessageTypes.FundingSigned, typeof(FundingSignedMessage));
        _messageTypeDictionary.Add(MessageTypes.OpenChannel, typeof(OpenChannel1Message));
        _messageTypeDictionary.Add(MessageTypes.OpenChannel2, typeof(OpenChannel2Message));
        _messageTypeDictionary.Add(MessageTypes.OnionMessage, typeof(OnionMessageMessage));
        _messageTypeDictionary.Add(MessageTypes.PeerStorage, typeof(PeerStorageMessage));
        _messageTypeDictionary.Add(MessageTypes.PeerStorageRetrieval, typeof(PeerStorageRetrievalMessage));
        _messageTypeDictionary.Add(MessageTypes.Shutdown, typeof(ShutdownMessage));
        _messageTypeDictionary.Add(MessageTypes.SpliceAck, typeof(SpliceAckMessage));
        _messageTypeDictionary.Add(MessageTypes.SpliceInit, typeof(SpliceInitMessage));
        _messageTypeDictionary.Add(MessageTypes.SpliceLocked, typeof(SpliceLockedMessage));
        _messageTypeDictionary.Add(MessageTypes.StartBatch, typeof(StartBatchMessage));
        _messageTypeDictionary.Add(MessageTypes.Stfu, typeof(StfuMessage));
        _messageTypeDictionary.Add(MessageTypes.TxAbort, typeof(TxAbortMessage));
        _messageTypeDictionary.Add(MessageTypes.TxAckRbf, typeof(TxAckRbfMessage));
        _messageTypeDictionary.Add(MessageTypes.TxAddOutput, typeof(TxAddOutputMessage));
        _messageTypeDictionary.Add(MessageTypes.TxComplete, typeof(TxCompleteMessage));
        _messageTypeDictionary.Add(MessageTypes.TxInitRbf, typeof(TxInitRbfMessage));
        _messageTypeDictionary.Add(MessageTypes.TxRemoveInput, typeof(TxRemoveInputMessage));
        _messageTypeDictionary.Add(MessageTypes.TxRemoveOutput, typeof(TxRemoveOutputMessage));
        _messageTypeDictionary.Add(MessageTypes.TxSignatures, typeof(TxSignaturesMessage));

        _messageTypeDictionary.Add(MessageTypes.ChannelAnnouncement, typeof(ChannelAnnouncementMessage));
        _messageTypeDictionary.Add(MessageTypes.NodeAnnouncement, typeof(NodeAnnouncementMessage));
        _messageTypeDictionary.Add(MessageTypes.ChannelUpdate, typeof(ChannelUpdateMessage));
        _messageTypeDictionary.Add(MessageTypes.AnnouncementSignatures, typeof(AnnouncementSignaturesMessage));
        _messageTypeDictionary.Add(MessageTypes.QueryShortChannelIds, typeof(QueryShortChannelIdsMessage));
        _messageTypeDictionary.Add(MessageTypes.ReplyShortChannelIdsEnd, typeof(ReplyShortChannelIdsEndMessage));
        _messageTypeDictionary.Add(MessageTypes.QueryChannelRange, typeof(QueryChannelRangeMessage));
        _messageTypeDictionary.Add(MessageTypes.ReplyChannelRange, typeof(ReplyChannelRangeMessage));
        _messageTypeDictionary.Add(MessageTypes.GossipTimestampFilter, typeof(GossipTimestampFilterMessage));
    }
}