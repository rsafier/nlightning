using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Bitcoin.SilentPayments.Models;
using Domain.Client.Responses;

[MessagePackObject]
public sealed class SilentPaymentIpcResponse
{
    [Key(0)] public string? Address { get; init; }
    [Key(1)] public uint? Label { get; init; }
    [Key(2)] public string? LabelName { get; init; }
    [Key(3)] public bool RecoverableElsewhere { get; init; }
    [Key(4)] public List<SilentPaymentLabelIpcInfo>? Labels { get; init; }
    [Key(5)] public SilentPaymentStatusIpcInfo? Status { get; init; }

    public static SilentPaymentIpcResponse FromClientResponse(SilentPaymentClientResponse response) => new()
    {
        Address = response.Address?.Address,
        Label = response.Address?.Label,
        LabelName = response.Address?.LabelName,
        RecoverableElsewhere = response.Address?.RecoverableElsewhere ?? response.Status?.RecoverableElsewhere ?? false,
        Labels = response.Labels?.Select(label => new SilentPaymentLabelIpcInfo
        {
            M = label.Label,
            Name = label.Name,
            CreatedAtHeight = label.CreatedAtHeight,
            Address = label.Address,
            IsChange = label.IsChange
        }).ToList(),
        Status = response.Status is { } status ? SilentPaymentStatusIpcInfo.FromDomain(status) : null
    };
}

[MessagePackObject]
public sealed class SilentPaymentLabelIpcInfo
{
    [Key(0)] public uint M { get; init; }
    [Key(1)] public string? Name { get; init; }
    [Key(2)] public uint CreatedAtHeight { get; init; }
    [Key(3)] public string? Address { get; init; }
    [Key(4)] public bool IsChange { get; init; }
}

[MessagePackObject]
public sealed class SilentPaymentStatusIpcInfo
{
    [Key(0)] public bool Enabled { get; init; }
    [Key(1)] public bool Send { get; init; }
    [Key(2)] public bool Receive { get; init; }
    [Key(3)] public bool RecoverableElsewhere { get; init; }
    [Key(4)] public uint? BirthdayHeight { get; init; }
    [Key(5)] public uint? LiveFromHeight { get; init; }
    [Key(6)] public uint? LiveCursorHeight { get; init; }
    [Key(7)] public uint? RescanCursorHeight { get; init; }
    [Key(8)] public uint? RescanTargetHeight { get; init; }
    [Key(9)] public uint RecoveryLabelCount { get; init; }
    [Key(10)] public string? PrevoutSource { get; init; }
    [Key(11)] public int FoundOutputs { get; init; }
    [Key(12)] public int IgnoredOutputs { get; init; }
    [Key(13)] public int UnspentOutputs { get; init; }
    [Key(14)] public double? LastScanMilliseconds { get; init; }
    [Key(15)] public string? LastError { get; init; }

    /// <summary>The unspent outputs (NL-1296), or null from an older daemon.</summary>
    [Key(16)] public List<SilentPaymentUnspentIpcInfo>? Unspent { get; init; }

    public static SilentPaymentStatusIpcInfo FromDomain(SilentPaymentStatus status) => new()
    {
        Enabled = status.Enabled,
        Send = status.Send,
        Receive = status.Receive,
        RecoverableElsewhere = status.RecoverableElsewhere,
        BirthdayHeight = status.BirthdayHeight,
        LiveFromHeight = status.LiveFromHeight,
        LiveCursorHeight = status.LiveCursorHeight,
        RescanCursorHeight = status.RescanCursorHeight,
        RescanTargetHeight = status.RescanTargetHeight,
        RecoveryLabelCount = status.RecoveryLabelCount,
        PrevoutSource = status.PrevoutSource,
        FoundOutputs = status.FoundOutputs,
        IgnoredOutputs = status.IgnoredOutputs,
        UnspentOutputs = status.UnspentOutputs,
        LastScanMilliseconds = status.LastScanMilliseconds,
        LastError = status.LastError,
        Unspent = status.Unspent.Select(output => new SilentPaymentUnspentIpcInfo
        {
            TxId = output.TxId,
            Index = output.Index,
            AmountSats = output.AmountSats,
            BlockHeight = output.BlockHeight,
            Label = output.Label
        }).ToList()
    };
}

[MessagePackObject]
public sealed class SilentPaymentUnspentIpcInfo
{
    /// <summary>The txid in display order.</summary>
    [Key(0)] public string? TxId { get; init; }
    [Key(1)] public uint Index { get; init; }
    [Key(2)] public long AmountSats { get; init; }
    [Key(3)] public uint BlockHeight { get; init; }
    [Key(4)] public uint? Label { get; init; }
}