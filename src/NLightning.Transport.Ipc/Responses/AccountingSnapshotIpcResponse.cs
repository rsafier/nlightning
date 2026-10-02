using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Accounting.Models;
using Domain.Client.Responses;

/// <summary>
/// Response for AccountingSnapshot (ClientCommand 42, NL-602): the node's live balances by bucket, in msat. Keys are
/// append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingSnapshotIpcResponse
{
    /// <summary>When it was taken, Unix milliseconds.</summary>
    [Key(0)] public required long TakenAtUnixMilliseconds { get; init; }

    /// <summary>The chain monitor's last processed block (0 when unknown).</summary>
    [Key(1)] public required uint BlockHeight { get; init; }

    /// <summary>One bucket per channel.</summary>
    [Key(2)] public required List<AccountingChannelBucketIpc> Channels { get; init; }

    /// <summary>Wallet outputs with at least 3 confirmations.</summary>
    [Key(3)] public required long WalletConfirmedMsat { get; init; }

    /// <summary>The other wallet outputs.</summary>
    [Key(4)] public required long WalletUnconfirmedMsat { get; init; }

    /// <summary>Wallet outputs locked to a funding or reserved for a fee (part of the two above).</summary>
    [Key(5)] public required long WalletLockedMsat { get; init; }

    /// <summary>Our gross balance over every channel whose funding is not spent.</summary>
    [Key(6)] public required long ChannelLocalMsat { get; init; }

    /// <summary>Our outputs of force closes waiting for their sweep.</summary>
    [Key(7)] public required long PendingOnchainMsat { get; init; }

    /// <summary>HTLC outputs of force closes not resolved yet.</summary>
    [Key(8)] public required long PendingHtlcOnchainMsat { get; init; }

    /// <summary>How many outputs of force closes wait for a resolution of ours.</summary>
    [Key(9)] public required int PendingSweepCount { get; init; }

    /// <summary>Channels, pending on-chain funds and the wallet (confirmed and unconfirmed) together.</summary>
    [Key(10)] public required long TotalMsat { get; init; }

    public static AccountingSnapshotIpcResponse FromClientResponse(AccountingSnapshotClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var snapshot = response.Snapshot;
        return new AccountingSnapshotIpcResponse
        {
            TakenAtUnixMilliseconds = snapshot.TakenAt.ToUnixTimeMilliseconds(),
            BlockHeight = snapshot.BlockHeight,
            Channels = snapshot.Channels.Select(AccountingChannelBucketIpc.FromModel).ToList(),
            WalletConfirmedMsat = snapshot.Wallet.ConfirmedMsat,
            WalletUnconfirmedMsat = snapshot.Wallet.UnconfirmedMsat,
            WalletLockedMsat = snapshot.Wallet.LockedMsat,
            ChannelLocalMsat = snapshot.ChannelLocalMsat,
            PendingOnchainMsat = snapshot.PendingOnchainMsat,
            PendingHtlcOnchainMsat = snapshot.PendingHtlcOnchainMsat,
            PendingSweepCount = snapshot.PendingSweepCount,
            TotalMsat = snapshot.TotalMsat
        };
    }
}

/// <summary>One channel's balances in an <see cref="AccountingSnapshotIpcResponse"/> (NL-602).</summary>
[MessagePackObject]
public sealed class AccountingChannelBucketIpc
{
    /// <summary>The channel, 64 hex characters.</summary>
    [Key(0)] public required string ChannelId { get; init; }

    /// <summary>The short channel id as <c>block x tx x output</c>, when known.</summary>
    [Key(1)] public string? ShortChannelId { get; init; }

    /// <summary>The <c>ChannelState</c> value.</summary>
    [Key(2)] public required byte State { get; init; }

    /// <summary>The <c>ChannelState</c> name.</summary>
    [Key(3)] public required string StateName { get; init; }

    /// <summary>The peer, 66 hex characters, when the channel is loaded.</summary>
    [Key(4)] public string? Counterparty { get; init; }

    [Key(5)] public required long CapacityMsat { get; init; }

    /// <summary>Our gross balance (our offered HTLCs not final included).</summary>
    [Key(6)] public required long LocalBalanceMsat { get; init; }

    /// <summary>The peer's gross balance.</summary>
    [Key(7)] public required long RemoteBalanceMsat { get; init; }

    /// <summary>Our offered HTLCs not final yet.</summary>
    [Key(8)] public required long LocalInFlightMsat { get; init; }

    /// <summary>The peer's offered HTLCs not final yet.</summary>
    [Key(9)] public required long RemoteInFlightMsat { get; init; }

    /// <summary>Our outputs of the channel's force close waiting for their sweep.</summary>
    [Key(10)] public required long PendingOnchainMsat { get; init; }

    /// <summary>The channel's HTLC outputs on chain not resolved yet.</summary>
    [Key(11)] public required long PendingHtlcOnchainMsat { get; init; }

    /// <summary>How many outputs make up the two amounts above.</summary>
    [Key(12)] public required int PendingSweepCount { get; init; }

    /// <summary>Whether the channel is loaded (false: only its on-chain outputs are known).</summary>
    [Key(13)] public required bool IsLoaded { get; init; }

    public static AccountingChannelBucketIpc FromModel(ChannelBalanceBucket bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return new AccountingChannelBucketIpc
        {
            ChannelId = bucket.ChannelId.ToString(),
            ShortChannelId = bucket.ShortChannelId?.ToString(),
            State = (byte)bucket.State,
            StateName = bucket.State.ToString(),
            Counterparty = bucket.Counterparty?.ToString(),
            CapacityMsat = bucket.CapacityMsat,
            LocalBalanceMsat = bucket.LocalBalanceMsat,
            RemoteBalanceMsat = bucket.RemoteBalanceMsat,
            LocalInFlightMsat = bucket.LocalInFlightMsat,
            RemoteInFlightMsat = bucket.RemoteInFlightMsat,
            PendingOnchainMsat = bucket.PendingOnchainMsat,
            PendingHtlcOnchainMsat = bucket.PendingHtlcOnchainMsat,
            PendingSweepCount = bucket.PendingSweepCount,
            IsLoaded = bucket.IsLoaded
        };
    }
}