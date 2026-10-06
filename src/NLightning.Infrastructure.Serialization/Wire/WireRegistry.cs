using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Wire;

using Definitions;
using Domain.Protocol.Constants;

/// <summary>
/// The declarative message registry and the typed TLV value index derived from its message tables.
/// Definitions compose their own value codecs; registration has no converter binding or reflection.
/// </summary>
/// <remarks>
/// Every migrated message is registered here exactly once; <c>WireRegistryTests</c> fails the build when a
/// <see cref="MessageTypes"/> value is missing, which is what used to turn a message silently "unknown".
/// </remarks>
public sealed class WireRegistry
{
    private readonly Dictionary<MessageTypes, IMessageTypeSerializer> _byType = [];
    private readonly Dictionary<Type, IMessageTypeSerializer> _byMessageType = [];

    private readonly Dictionary<Type, TlvDef> _tlvDefinitions = [];

    public WireRegistry()
    {
        IMessageTypeSerializer[] defs =
        [
            PeerStorageWire.Def,
            PeerStorageRetrievalWire.Def,
            InitWire.Def,
            ErrorWire.Def,
            WarningWire.Def,
            PingWire.Def,
            PongWire.Def,
            UpdateAddHtlcWire.Def,
            UpdateFulfillHtlcWire.Def,
            UpdateFailHtlcWire.Def,
            UpdateFailMalformedHtlcWire.Def,
            CommitmentSignedWire.Def,
            RevokeAndAckWire.Def,
            UpdateFeeWire.Def,
            ChannelReestablishWire.Def,

            // The interactive-tx wave, in ascending message type order after tx_add_input
            TxAddInputWire.Def,
            TxAddOutputWire.Def,
            TxRemoveInputWire.Def,
            TxRemoveOutputWire.Def,
            TxCompleteWire.Def,
            TxSignaturesWire.Def,
            TxInitRbfWire.Def,
            TxAckRbfWire.Def,
            TxAbortWire.Def,

            // The BOLT 2 channel lifecycle wave, in ascending message type order
            StfuWire.Def,
            OpenChannelWire.Def,
            AcceptChannelWire.Def,
            FundingWire.Def,
            FundingSignedWire.Def,
            ChannelReadyWire.Def,
            ShutdownWire.Def,
            ClosingSignedWire.Def,
            ClosingCompleteWire.Def,
            ClosingSigWire.Def,
            OpenChannel2Wire.Def,
            AcceptChannel2Wire.Def,

            // The splicing/batching wave, in ascending message type order, then BOLT 4 onion_message
            SpliceLockedWire.Def,
            SpliceInitWire.Def,
            SpliceAckWire.Def,
            StartBatchWire.Def,
            OnionMessageWire.Def,

            // The BOLT 7 gossip wave, in ascending message type order
            ChannelAnnouncementWire.Def,
            NodeAnnouncementWire.Def,
            ChannelUpdateWire.Def,
            AnnouncementSignaturesWire.Def,
            QueryShortChannelIdsWire.Def,
            ReplyShortChannelIdsEndWire.Def,
            QueryChannelRangeWire.Def,
            ReplyChannelRangeWire.Def,
            GossipTimestampFilterWire.Def,

            // Taproot gossip (BOLTs PR #1059), pure TLV messages, in ascending message type order
            AnnouncementSignatures2Wire.Def,
            ChannelAnnouncement2Wire.Def,
            NodeAnnouncement2Wire.Def,
            ChannelUpdate2Wire.Def
        ];

        foreach (var tlv in HopTlvDefs.All)
            _tlvDefinitions.Add(tlv.RuntimeType!, tlv);

        foreach (var def in defs)
        {
            if (def is not MessageWireBase wire)
                throw new InvalidOperationException($"A wire definition of {def.GetType().Name} must derive {nameof(MessageWireBase)}.");

            if (!_byType.TryAdd(wire.Type, def))
                throw new InvalidOperationException($"Wire definition for {wire.Type} registered twice.");
            _byMessageType[wire.MessageType] = def;
            foreach (var tlv in wire.TlvDefinitions)
                if (tlv.RuntimeType is { } runtimeType)
                    _tlvDefinitions.TryAdd(runtimeType, tlv.ValueDefinition);
        }
    }

    public IMessageTypeSerializer? Get(MessageTypes type) => _byType.GetValueOrDefault(type);

    public IMessageTypeSerializer? Get<TMessage>() where TMessage : IMessage
        => _byMessageType.GetValueOrDefault(typeof(TMessage));

    /// <summary>Typed value definitions composed by peer messages and the dedicated hop codec.</summary>
    public IReadOnlyCollection<Type> TlvTypes => _tlvDefinitions.Keys;

    public TlvDef? GetTlvDefinition(Type runtimeType) => _tlvDefinitions.GetValueOrDefault(runtimeType);

    public TlvDef<TTlv>? GetTlvDefinition<TTlv>() where TTlv : Domain.Protocol.Tlv.BaseTlv
        => GetTlvDefinition(typeof(TTlv)) as TlvDef<TTlv>;

    /// <summary>Every registered definition's wire type (the completeness tests walk this).</summary>
    public IEnumerable<MessageTypes> Types => _byType.Keys;
}

/// <summary>
/// The non-generic seam the registry uses to read message types and their TLV definitions; the
/// generic <see cref="MessageWire{TMessage}"/> is both this and the <c>IMessageTypeSerializer</c> callers get back.
/// </summary>
public abstract class MessageWireBase
{
    public abstract MessageTypes Type { get; }

    public abstract Type MessageType { get; }

    public abstract IEnumerable<TlvDef> TlvDefinitions { get; }
}