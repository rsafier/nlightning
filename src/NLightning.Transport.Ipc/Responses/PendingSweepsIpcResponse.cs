using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Onchain.Enums;

/// <summary>
/// Response for PendingSweeps (ClientCommand 15). Txids are hex in the usual display order.
/// </summary>
[MessagePackObject]
public sealed class PendingSweepsIpcResponse
{
    [Key(0)] public required List<PendingSweepChannelIpcInfo> Channels { get; init; }

    public static PendingSweepsIpcResponse FromClientResponse(PendingSweepsClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new PendingSweepsIpcResponse
        {
            Channels = clientResponse.Channels.Select(c => new PendingSweepChannelIpcInfo
            {
                ChannelId = c.ChannelId,
                State = c.State,
                CloseKind = c.CloseKind,
                CommitmentTxId = ToDisplay(c.CommitmentTxId),
                CommitmentNumber = c.CommitmentNumber,
                SpentAtHeight = c.SpentAtHeight,
                Outputs = c.Outputs.Select(o => new PendingSweepOutputIpcInfo
                {
                    TransactionId = ToDisplay(o.TransactionId),
                    OutputIndex = o.OutputIndex,
                    Descriptor = o.Descriptor,
                    State = o.State,
                    AmountSat = o.AmountSat,
                    HtlcDirection = o.HtlcDirection,
                    HtlcId = o.HtlcId,
                    ResolvingTxId = o.ResolvingTxId is { } resolving ? ToDisplay(resolving) : null,
                    WaitUntilHeight = o.WaitUntilHeight,
                    DeadlineHeight = o.DeadlineHeight,
                    ResolvedHeight = o.ResolvedHeight
                }).ToList()
            }).ToList()
        };
    }

    /// <summary>A txid in the display (RPC, block explorer) byte order.</summary>
    public static string ToDisplay(TxId txId) =>
        Convert.ToHexString(((byte[])txId).Reverse().ToArray()).ToLowerInvariant();
}

/// <summary>One channel of a <see cref="PendingSweepsIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class PendingSweepChannelIpcInfo
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required ChannelState State { get; init; }
    [Key(2)] public required ChannelCloseKind CloseKind { get; init; }
    [Key(3)] public required string CommitmentTxId { get; init; }
    [Key(4)] public ulong? CommitmentNumber { get; init; }
    [Key(5)] public uint SpentAtHeight { get; init; }
    [Key(6)] public required List<PendingSweepOutputIpcInfo> Outputs { get; init; }
}

/// <summary>One output of a <see cref="PendingSweepChannelIpcInfo"/>.</summary>
[MessagePackObject]
public sealed class PendingSweepOutputIpcInfo
{
    [Key(0)] public required string TransactionId { get; init; }
    [Key(1)] public uint OutputIndex { get; init; }
    [Key(2)] public OutputDescriptorKind Descriptor { get; init; }
    [Key(3)] public OutputResolutionState State { get; init; }
    [Key(4)] public ulong? AmountSat { get; init; }
    [Key(5)] public HtlcDirection? HtlcDirection { get; init; }
    [Key(6)] public ulong? HtlcId { get; init; }
    [Key(7)] public string? ResolvingTxId { get; init; }
    [Key(8)] public uint? WaitUntilHeight { get; init; }
    [Key(9)] public uint? DeadlineHeight { get; init; }
    [Key(10)] public uint? ResolvedHeight { get; init; }
}