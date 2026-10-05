namespace NLightning.Infrastructure.Serialization.Tests.Wire;

using Domain.Protocol.Constants;
using Helpers;

/// <summary>
/// The registry-completeness guard: every migrated message must resolve through the merged
/// <c>IMessageTypeSerializerFactory</c> in both lookup directions. This is the property that makes the old silent
/// "missing registration turns a message unknown" failure mode a build-time test failure instead (plan
/// <c>docs/agents/CODEC_REDESIGN_PLAN.md</c> §4).
/// </summary>
public class WireRegistryTests
{
    /// <summary>The wire types the P0 vertical slice, the BOLT 2 channel wave and the P2 wave migrated.</summary>
    public static TheoryData<MessageTypes> MigratedTypes => new()
    {
        MessageTypes.Init, MessageTypes.Error, MessageTypes.Warning, MessageTypes.Ping, MessageTypes.Pong,
        MessageTypes.UpdateAddHtlc, MessageTypes.UpdateFulfillHtlc, MessageTypes.UpdateFailHtlc,
        MessageTypes.UpdateFailMalformedHtlc, MessageTypes.CommitmentSigned, MessageTypes.RevokeAndAck,
        MessageTypes.UpdateFee, MessageTypes.ChannelReestablish, MessageTypes.TxAddInput,
        MessageTypes.Stfu, MessageTypes.OpenChannel, MessageTypes.AcceptChannel, MessageTypes.FundingCreated,
        MessageTypes.FundingSigned, MessageTypes.ChannelReady, MessageTypes.Shutdown, MessageTypes.ClosingSigned,
        MessageTypes.ClosingComplete, MessageTypes.ClosingSig, MessageTypes.OpenChannel2, MessageTypes.AcceptChannel2,
        MessageTypes.PeerStorage, MessageTypes.PeerStorageRetrieval, MessageTypes.TxAddOutput,
        MessageTypes.TxRemoveInput, MessageTypes.TxRemoveOutput, MessageTypes.TxComplete, MessageTypes.TxSignatures,
        MessageTypes.TxInitRbf, MessageTypes.TxAckRbf, MessageTypes.TxAbort, MessageTypes.SpliceLocked,
        MessageTypes.SpliceInit, MessageTypes.SpliceAck, MessageTypes.StartBatch, MessageTypes.OnionMessage
    };

    [Theory]
    [MemberData(nameof(MigratedTypes))]
    public void Given_MigratedType_When_LookedUpByWireType_Then_TheRegistryServesIt(MessageTypes type)
    {
        var serializer = SerializerHelper.MessageTypeSerializerFactory.GetSerializer(type);

        Assert.NotNull(serializer);
    }

    [Fact]
    public void Given_SampleMigratedTypes_When_LookedUpByMessageType_Then_TheSameInstanceServesBothDirections()
    {
        // Both lookup directions must agree, so MessageSerializer.DeserializeMessageAsync<T>'s wire-type check works
        Assert.Same(SerializerHelper.MessageTypeSerializerFactory.GetSerializer(MessageTypes.Init),
                    SerializerHelper.MessageTypeSerializerFactory
                       .GetSerializer<Domain.Protocol.Messages.InitMessage>());
        Assert.Same(SerializerHelper.MessageTypeSerializerFactory.GetSerializer(MessageTypes.UpdateFee),
                    SerializerHelper.MessageTypeSerializerFactory
                       .GetSerializer<Domain.Protocol.Messages.UpdateFeeMessage>());
        Assert.Same(SerializerHelper.MessageTypeSerializerFactory.GetSerializer(MessageTypes.RevokeAndAck),
                    SerializerHelper.MessageTypeSerializerFactory
                       .GetSerializer<Domain.Protocol.Messages.RevokeAndAckMessage>());
    }
}