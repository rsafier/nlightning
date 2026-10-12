namespace NLightning.Application.Tests.InteractiveTx.TestDoubles;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>Builds one well-formed message of each interactive-tx type (66-74).</summary>
internal static class InteractiveTxMessages
{
    public static readonly BitcoinScript P2WpkhScript = new([0x00, 0x14, .. Enumerable.Repeat((byte)0x42, 20)]);

    public static TheoryData<MessageTypes> NegotiationTypes =>
    [
        MessageTypes.TxAddInput, MessageTypes.TxAddOutput, MessageTypes.TxRemoveInput, MessageTypes.TxRemoveOutput,
        MessageTypes.TxComplete, MessageTypes.TxSignatures, MessageTypes.TxInitRbf, MessageTypes.TxAckRbf
    ];

    public static IChannelMessage Create(MessageTypes type, ChannelId channelId) => type switch
    {
        MessageTypes.TxAddInput => new TxAddInputMessage(
            new TxAddInputPayload(channelId, 1, WalletUtxo.Create(10_000).PrevTx, 0, 0xFFFFFFFD)),
        MessageTypes.TxAddOutput => new TxAddOutputMessage(
            new TxAddOutputPayload(LightningMoney.Satoshis(10_000), channelId, P2WpkhScript, 1)),
        MessageTypes.TxRemoveInput => new TxRemoveInputMessage(new TxRemoveInputPayload(channelId, 1)),
        MessageTypes.TxRemoveOutput => new TxRemoveOutputMessage(new TxRemoveOutputPayload(channelId, 1)),
        MessageTypes.TxComplete => new TxCompleteMessage(new TxCompletePayload(channelId)),
        MessageTypes.TxSignatures => new TxSignaturesMessage(new TxSignaturesPayload(channelId, new byte[32], [])),
        MessageTypes.TxInitRbf => new TxInitRbfMessage(new TxInitRbfPayload(channelId, 1_000, 0)),
        MessageTypes.TxAckRbf => new TxAckRbfMessage(new TxAckRbfPayload(channelId)),
        MessageTypes.TxAbort => new TxAbortMessage(new TxAbortPayload(channelId, "stop"u8.ToArray())),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}