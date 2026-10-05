using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Wire;

using Definitions;
using Domain.Protocol.Constants;

/// <summary>
/// The registry of migrated wire definitions. <see cref="MessageTypeSerializerFactory"/> consults it first and
/// falls back to the hand-written serializers, so migrated and unmigrated messages coexist behind the same
/// <c>IMessageSerializer</c> API (plan <c>docs/agents/CODEC_REDESIGN_PLAN.md</c>).
/// </summary>
/// <remarks>
/// Every migrated message is registered here exactly once; <c>WireRegistryTests</c> fails the build when a
/// <see cref="MessageTypes"/> value is missing, which is what used to turn a message silently "unknown".
/// </remarks>
public sealed class WireRegistry
{
    private readonly Dictionary<MessageTypes, IMessageTypeSerializer> _byType = [];
    private readonly Dictionary<Type, IMessageTypeSerializer> _byMessageType = [];

    public WireRegistry(ITlvConverterFactory converters)
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
            OnionMessageWire.Def
        ];

        foreach (var def in defs)
        {
            if (def is not MessageWireBase wire)
                throw new InvalidOperationException($"A wire definition of {def.GetType().Name} must derive {nameof(MessageWireBase)}.");

            wire.Bind(converters);
            if (!_byType.TryAdd(wire.Type, def))
                throw new InvalidOperationException($"Wire definition for {wire.Type} registered twice.");
            _byMessageType[wire.MessageType] = def;
        }
    }

    public IMessageTypeSerializer? Get(MessageTypes type) => _byType.GetValueOrDefault(type);

    public IMessageTypeSerializer? Get<TMessage>() where TMessage : IMessage
        => _byMessageType.GetValueOrDefault(typeof(TMessage));

    /// <summary>Every registered definition's wire type (the completeness tests walk this).</summary>
    public IEnumerable<MessageTypes> Types => _byType.Keys;
}

/// <summary>
/// The non-generic seam the registry uses to bind a definition's TLV converter factory and read its type; the
/// generic <see cref="MessageWire{TMessage}"/> is both this and the <c>IMessageTypeSerializer</c> callers get back.
/// </summary>
public abstract class MessageWireBase
{
    public abstract MessageTypes Type { get; }

    public abstract Type MessageType { get; }

    internal abstract void Bind(ITlvConverterFactory converters);
}