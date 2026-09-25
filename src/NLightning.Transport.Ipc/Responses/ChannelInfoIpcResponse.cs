using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// One channel in a <see cref="ListChannelsIpcResponse"/>.
/// </summary>
[MessagePackObject]
public sealed class ChannelInfoIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required CompactPubKey PeerId { get; init; }
    [Key(2)] public required ChannelState State { get; init; }
    [Key(3)] public bool IsInitiator { get; init; }
    [Key(4)] public bool IsPeerConnected { get; init; }

    /// <summary>
    /// The short channel id as a BOLT 7 uint64 (block &lt;&lt; 40 | tx index &lt;&lt; 16 | output), the same number LND
    /// reports as <c>chan_id</c>; null until the funding transaction confirms.
    /// </summary>
    [Key(5)] public ulong? ShortChannelId { get; init; }

    [Key(6)] public TxId? FundingTxId { get; init; }
    [Key(7)] public ushort? FundingOutputIndex { get; init; }
    [Key(8)] public required LightningMoney Capacity { get; init; }
    [Key(9)] public required LightningMoney LocalBalance { get; init; }
    [Key(10)] public required LightningMoney RemoteBalance { get; init; }
    [Key(11)] public ulong LocalCommitmentNumber { get; init; }
    [Key(12)] public ulong RemoteCommitmentNumber { get; init; }
    [Key(13)] public int OfferedHtlcCount { get; init; }
    [Key(14)] public int ReceivedHtlcCount { get; init; }
    [Key(15)] public bool DataLossDetected { get; init; }

    public static ChannelInfoIpcResponse FromClientResponse(ChannelInfoClientResponse channel)
    {
        return new ChannelInfoIpcResponse
        {
            ChannelId = channel.ChannelId,
            PeerId = channel.PeerId,
            State = channel.State,
            IsInitiator = channel.IsInitiator,
            IsPeerConnected = channel.IsPeerConnected,
            ShortChannelId = channel.ShortChannelId is { } scid
                                 ? ((ulong)scid.BlockHeight << 40) | ((ulong)scid.TransactionIndex << 16)
                                                                   | scid.OutputIndex
                                 : null,
            FundingTxId = channel.FundingTxId,
            FundingOutputIndex = channel.FundingOutputIndex,
            Capacity = channel.Capacity,
            LocalBalance = channel.LocalBalance,
            RemoteBalance = channel.RemoteBalance,
            LocalCommitmentNumber = channel.LocalCommitmentNumber,
            RemoteCommitmentNumber = channel.RemoteCommitmentNumber,
            OfferedHtlcCount = channel.OfferedHtlcCount,
            ReceivedHtlcCount = channel.ReceivedHtlcCount,
            DataLossDetected = channel.DataLossDetected
        };
    }
}