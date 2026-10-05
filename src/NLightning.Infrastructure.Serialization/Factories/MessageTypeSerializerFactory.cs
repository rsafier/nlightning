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