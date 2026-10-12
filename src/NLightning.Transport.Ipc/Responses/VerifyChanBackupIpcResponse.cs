using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Response for VerifyChanBackup (ClientCommand 22). Txids are hex in the display order. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class VerifyChanBackupIpcResponse
{
    [Key(0)] public bool IsValid { get; init; }
    [Key(1)] public string? Error { get; init; }

    /// <summary>When the backup was made (UNIX seconds), when it decrypted.</summary>
    [Key(2)] public long? CreatedAt { get; init; }

    [Key(3)] public required List<ChanBackupChannelIpcInfo> Channels { get; init; }

    public static VerifyChanBackupIpcResponse FromClientResponse(VerifyChanBackupClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new VerifyChanBackupIpcResponse
        {
            IsValid = clientResponse.IsValid,
            Error = clientResponse.Error,
            CreatedAt = clientResponse.CreatedAt?.ToUnixTimeSeconds(),
            Channels = clientResponse.Channels.Select(c => new ChanBackupChannelIpcInfo
            {
                ChannelId = c.ChannelId,
                RemoteNodeId = c.RemoteNodeId,
                Addresses = c.Addresses.ToList(),
                FundingTxId = PendingSweepsIpcResponse.ToDisplay(c.FundingTxId),
                FundingOutputIndex = c.FundingOutputIndex,
                CapacitySat = c.CapacitySat,
                ShortChannelId = c.ShortChannelId is { } scid
                                     ? ListGraphChannelsIpcResponse.ToNumber(scid)
                                     : null,
                IsInitiator = c.IsInitiator,
                OptionAnchors = c.OptionAnchors,
                KeysMatch = c.KeysMatch,
                LocalState = c.LocalState,
                OptionSimpleTaproot = c.OptionSimpleTaproot
            }).ToList()
        };
    }
}

/// <summary>One channel of a <see cref="VerifyChanBackupIpcResponse"/>.</summary>
[MessagePackObject]
public sealed class ChanBackupChannelIpcInfo
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required CompactPubKey RemoteNodeId { get; init; }
    [Key(2)] public required List<string> Addresses { get; init; }
    [Key(3)] public required string FundingTxId { get; init; }
    [Key(4)] public ushort FundingOutputIndex { get; init; }
    [Key(5)] public ulong CapacitySat { get; init; }
    [Key(6)] public ulong? ShortChannelId { get; init; }
    [Key(7)] public bool IsInitiator { get; init; }
    [Key(8)] public bool OptionAnchors { get; init; }
    [Key(9)] public bool KeysMatch { get; init; }
    [Key(10)] public ChannelState? LocalState { get; init; }

    /// <summary>Whether it is a simple taproot channel (NL-877 T5).</summary>
    [Key(11)] public bool OptionSimpleTaproot { get; init; }

    /// <summary>The channel type's name as the client prints it.</summary>
    public static string TypeName(bool optionSimpleTaproot, bool optionAnchors) =>
        optionSimpleTaproot ? "simple_taproot" : optionAnchors ? "anchors" : "static_remotekey";
}