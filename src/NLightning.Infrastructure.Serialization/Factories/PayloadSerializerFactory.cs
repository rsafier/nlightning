using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Factories;

using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;
using Interfaces;
using Payloads;

public class PayloadSerializerFactory : IPayloadSerializerFactory
{
    private readonly IFeatureSetSerializer _featureSetSerializer;
    private readonly Dictionary<Type, IPayloadSerializer> _serializers = new();
    private readonly Dictionary<MessageTypes, Type> _messageTypeDictionary = new();
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public PayloadSerializerFactory(IFeatureSetSerializer featureSetSerializer,
                                    IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _featureSetSerializer = featureSetSerializer;
        _valueObjectSerializerFactory = valueObjectSerializerFactory;

        RegisterSerializers();
        RegisterTypeDictionary();
    }

    public IPayloadSerializer<TPayloadType>? GetSerializer<TPayloadType>() where TPayloadType : IMessagePayload
    {
        return _serializers.GetValueOrDefault(typeof(TPayloadType)) as IPayloadSerializer<TPayloadType>;
    }

    public IPayloadSerializer? GetSerializer(MessageTypes messageType)
    {
        var type = _messageTypeDictionary.GetValueOrDefault(messageType);
        return type is null ? null : _serializers.GetValueOrDefault(type);
    }

    private void RegisterSerializers()
    {
        _serializers.Add(typeof(AnnouncementSignaturesPayload), new AnnouncementSignaturesPayloadSerializer());
        _serializers.Add(typeof(ChannelAnnouncementPayload), new ChannelAnnouncementPayloadSerializer());
        _serializers.Add(typeof(ChannelUpdatePayload), new ChannelUpdatePayloadSerializer());
        _serializers.Add(typeof(NodeAnnouncementPayload), new NodeAnnouncementPayloadSerializer());
        _serializers.Add(typeof(GossipTimestampFilterPayload),
                         new GossipTimestampFilterPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(OnionMessagePayload), new OnionMessagePayloadSerializer());
        _serializers.Add(typeof(PeerStoragePayload), new PeerStoragePayloadSerializer());
        _serializers.Add(typeof(PeerStorageRetrievalPayload), new PeerStorageRetrievalPayloadSerializer());
        _serializers.Add(typeof(QueryChannelRangePayload),
                         new QueryChannelRangePayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(QueryShortChannelIdsPayload),
                         new QueryShortChannelIdsPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(ReplyChannelRangePayload),
                         new ReplyChannelRangePayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(ReplyShortChannelIdsEndPayload),
                         new ReplyShortChannelIdsEndPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(SpliceAckPayload), new SpliceAckPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(SpliceInitPayload), new SpliceInitPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(SpliceLockedPayload),
                         new SpliceLockedPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(StartBatchPayload), new StartBatchPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxAbortPayload), new TxAbortPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxAckRbfPayload), new TxAckRbfPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxAddOutputPayload), new TxAddOutputPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxCompletePayload), new TxCompletePayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxInitRbfPayload), new TxInitRbfPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxRemoveInputPayload),
                         new TxRemoveInputPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxRemoveOutputPayload),
                         new TxRemoveOutputPayloadSerializer(_valueObjectSerializerFactory));
        _serializers.Add(typeof(TxSignaturesPayload), new TxSignaturesPayloadSerializer(_valueObjectSerializerFactory));
    }

    private void RegisterTypeDictionary()
    {
        _messageTypeDictionary.Add(MessageTypes.OnionMessage, typeof(OnionMessagePayload));
        _messageTypeDictionary.Add(MessageTypes.PeerStorage, typeof(PeerStoragePayload));
        _messageTypeDictionary.Add(MessageTypes.PeerStorageRetrieval, typeof(PeerStorageRetrievalPayload));
        _messageTypeDictionary.Add(MessageTypes.SpliceAck, typeof(SpliceAckPayload));
        _messageTypeDictionary.Add(MessageTypes.SpliceInit, typeof(SpliceInitPayload));
        _messageTypeDictionary.Add(MessageTypes.SpliceLocked, typeof(SpliceLockedPayload));
        _messageTypeDictionary.Add(MessageTypes.StartBatch, typeof(StartBatchPayload));
        _messageTypeDictionary.Add(MessageTypes.TxAbort, typeof(TxAbortPayload));
        _messageTypeDictionary.Add(MessageTypes.TxAckRbf, typeof(TxAckRbfPayload));
        _messageTypeDictionary.Add(MessageTypes.TxAddOutput, typeof(TxAddOutputPayload));
        _messageTypeDictionary.Add(MessageTypes.TxComplete, typeof(TxCompletePayload));
        _messageTypeDictionary.Add(MessageTypes.TxInitRbf, typeof(TxInitRbfPayload));
        _messageTypeDictionary.Add(MessageTypes.TxRemoveInput, typeof(TxRemoveInputPayload));
        _messageTypeDictionary.Add(MessageTypes.TxRemoveOutput, typeof(TxRemoveOutputPayload));
        _messageTypeDictionary.Add(MessageTypes.TxSignatures, typeof(TxSignaturesPayload));

        // BOLT 7: gossip queries, announcements and channel_update are parsed
        _messageTypeDictionary.Add(MessageTypes.QueryShortChannelIds, typeof(QueryShortChannelIdsPayload));
        _messageTypeDictionary.Add(MessageTypes.ReplyShortChannelIdsEnd, typeof(ReplyShortChannelIdsEndPayload));
        _messageTypeDictionary.Add(MessageTypes.QueryChannelRange, typeof(QueryChannelRangePayload));
        _messageTypeDictionary.Add(MessageTypes.ReplyChannelRange, typeof(ReplyChannelRangePayload));
        _messageTypeDictionary.Add(MessageTypes.GossipTimestampFilter, typeof(GossipTimestampFilterPayload));
        _messageTypeDictionary.Add(MessageTypes.ChannelUpdate, typeof(ChannelUpdatePayload));
        _messageTypeDictionary.Add(MessageTypes.ChannelAnnouncement, typeof(ChannelAnnouncementPayload));
        _messageTypeDictionary.Add(MessageTypes.NodeAnnouncement, typeof(NodeAnnouncementPayload));
        _messageTypeDictionary.Add(MessageTypes.AnnouncementSignatures, typeof(AnnouncementSignaturesPayload));
    }
}