using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Accounting;

using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Onchain.Accounting;

/// <summary>
/// The node's live balances by bucket (NL-602, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §7 IPC 42): one bucket per
/// channel loaded in <see cref="IChannelMemoryRepository"/> (balances and in-flight HTLCs from its commitment
/// snapshot), one per channel that is not loaded but still has outputs of a force close to resolve, and the wallet
/// (<see cref="IUtxoMemoryRepository"/>, the <c>walletbalance</c> numbers at the chain monitor's last block).
/// </summary>
/// <remarks>
/// <para>Pending on-chain funds are the channel's unresolved <c>OutputResolutions</c> rows (read in a scope of its
/// own) that are ours and not spent yet (<see cref="OutputResolutionState.Pending"/>, <c>Waiting</c>,
/// <c>Broadcast</c>): a <c>Resolved</c> output's money is already in the wallet (our sweep) or gone (the peer's
/// spend). HTLC outputs are reported apart: either side may still take them. Only the outputs the books count as
/// pending (<see cref="OnchainAccounting.CountsAtClose(OutputDescriptorKind, HtlcDirection?, bool)"/>, the rule the
/// close event's <c>countedVouts</c> and the resolutions follow) go into those two amounts; the others (the peer's
/// HTLCs, a revoked commitment's outputs, a fundee's anchor) are booked only once claimed and are reported as
/// <see cref="ChannelBalanceBucket.PendingUncountedMsat"/> (NL-618).</para>
/// <para>Closed and Stale channels are left out; a channel whose funding is spent (<see cref="ChannelState.OnchainResolving"/>)
/// reports no off-chain amount (see <see cref="ChannelBalanceBucket"/>).</para>
/// </remarks>
public sealed class NodeSnapshotSource : INodeSnapshotSource
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public NodeSnapshotSource(IChannelMemoryRepository channelMemoryRepository,
                              IUtxoMemoryRepository utxoMemoryRepository,
                              IServiceScopeFactory? scopeFactory = null,
                              IBlockchainMonitor? blockchainMonitor = null, TimeProvider? timeProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _utxoMemoryRepository = utxoMemoryRepository;
        _scopeFactory = scopeFactory;
        _blockchainMonitor = blockchainMonitor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AccountingSnapshot> TakeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var takenAt = _timeProvider.GetUtcNow();
        var height = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var pending = await ReadPendingOutputsAsync(cancellationToken);

        var buckets = new List<ChannelBalanceBucket>();
        var listed = new HashSet<ChannelId>();
        var channels = _channelMemoryRepository
                      .FindChannels(c => c.State is not (ChannelState.Closed or ChannelState.Stale))
                      .OrderBy(c => c.ChannelId.ToString(), StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!listed.Add(channel.ChannelId))
                continue;

            pending.Outputs.TryGetValue(channel.ChannelId, out var outputs);
            buckets.Add(ToBucket(channel, outputs));
        }

        // Channels resolved on chain that are no longer loaded (a restart keeps every non-Closed channel loaded, so
        // this is a channel removed from memory while outputs of it are still open)
        foreach (var (channelId, outputs) in pending.Outputs.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        {
            if (!listed.Add(channelId))
                continue;

            var (ours, htlcs, uncounted, count) = Sum(outputs, pending.Funders.GetValueOrDefault(channelId));
            buckets.Add(new ChannelBalanceBucket(channelId, null, ChannelState.OnchainResolving, null, 0, 0, 0, 0, 0,
                                                 ours, htlcs, count, false, uncounted));
        }

        var wallet = new WalletBalanceBucket(ToMsat(_utxoMemoryRepository.GetConfirmedBalance(height).MilliSatoshi),
                                             ToMsat(_utxoMemoryRepository.GetUnconfirmedBalance(height).MilliSatoshi),
                                             ToMsat(_utxoMemoryRepository.GetLockedBalance().MilliSatoshi));
        return new AccountingSnapshot(takenAt, height, buckets, wallet);
    }

    /// <summary>Whether an output of a force close is ours to take (or to fight for: HTLC outputs).</summary>
    internal static bool IsOurs(OutputDescriptorKind kind) =>
        kind is not (OutputDescriptorKind.Unknown or OutputDescriptorKind.PeerOutput or OutputDescriptorKind.PeerAnchor);

    /// <summary>Whether an output of ours is an HTLC whose outcome is not decided yet.</summary>
    internal static bool IsHtlc(OutputDescriptorKind kind) =>
        kind is OutputDescriptorKind.LocalOfferedHtlc or OutputDescriptorKind.LocalReceivedHtlc
             or OutputDescriptorKind.RemoteReceivedHtlc or OutputDescriptorKind.RemoteOfferedHtlc;

    private static ChannelBalanceBucket ToBucket(ChannelModel channel, List<OutputResolutionModel>? outputs)
    {
        var (pendingOnchain, pendingHtlcs, uncounted, count) = Sum(outputs, channel.IsInitiator);
        // A default ShortChannelId has no bytes; block 0 never holds a funding transaction
        var scid = channel.ShortChannelId.BlockHeight == 0 ? (ShortChannelId?)null : channel.ShortChannelId;
        var capacity = channel.FundingOutput is { } funding ? ToMsat(funding.Amount.MilliSatoshi) : 0;
        if (channel.State is ChannelState.OnchainResolving)
            return new ChannelBalanceBucket(channel.ChannelId, scid, channel.State, channel.RemoteNodeId, capacity, 0, 0,
                                            0, 0, pendingOnchain, pendingHtlcs, count, true, uncounted);

        long localInFlight = 0;
        long remoteInFlight = 0;
        if (channel.Commitments is { } commitments)
        {
            foreach (var htlc in commitments.Htlcs.Values)
            {
                if (HtlcStateTable.IsFinal(htlc.State))
                    continue;

                if (htlc.Direction == HtlcDirection.Outgoing)
                    localInFlight += ToMsat(htlc.AmountMsat);
                else
                    remoteInFlight += ToMsat(htlc.AmountMsat);
            }
        }

        return new ChannelBalanceBucket(channel.ChannelId, scid, channel.State, channel.RemoteNodeId, capacity,
                                        ToMsat(channel.LocalBalance.MilliSatoshi),
                                        ToMsat(channel.RemoteBalance.MilliSatoshi), localInFlight, remoteInFlight,
                                        pendingOnchain, pendingHtlcs, count, true, uncounted);
    }

    private static (long Ours, long Htlcs, long Uncounted, int Count) Sum(List<OutputResolutionModel>? outputs,
                                                                          bool weFund)
    {
        if (outputs is null)
            return (0, 0, 0, 0);

        long ours = 0;
        long htlcs = 0;
        long uncounted = 0;
        foreach (var output in outputs)
        {
            var amountMsat = ToMsat((OutputDescriptorData.TryDecode(output)?.AmountSat ?? 0) * 1_000);
            if (!OnchainAccounting.CountsAtClose(output.Descriptor, output.HtlcDirection, weFund))
                uncounted += amountMsat;
            else if (IsHtlc(output.Descriptor) || output.Descriptor == OutputDescriptorKind.RevokedHtlc)
                htlcs += amountMsat;
            else
                ours += amountMsat;
        }

        return (ours, htlcs, uncounted, outputs.Count);
    }

    private sealed record PendingOutputs(Dictionary<ChannelId, List<OutputResolutionModel>> Outputs,
                                         Dictionary<ChannelId, bool> Funders);

    private async Task<PendingOutputs> ReadPendingOutputsAsync(CancellationToken cancellationToken)
    {
        var pending = new Dictionary<ChannelId, List<OutputResolutionModel>>();
        var funders = new Dictionary<ChannelId, bool>();
        if (_scopeFactory is null)
            return new PendingOutputs(pending, funders);

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var outputs = await unitOfWork.OnchainResolutionDbRepository.GetUnresolvedOutputsAsync();

        foreach (var output in outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOurs(output.Descriptor)
             || output.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting
                                                                  or OutputResolutionState.Broadcast))
                continue;

            if (!pending.TryGetValue(output.ChannelId, out var list))
                pending[output.ChannelId] = list = [];
            list.Add(output);
        }

        // Whether we funded a channel that is no longer loaded (only its anchor depends on it, NL-618)
        foreach (var channelId in pending.Keys)
        {
            if (_channelMemoryRepository.TryGetChannel(channelId, out _))
                continue;

            try
            {
                if (await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId) is { } stored)
                    funders[channelId] = stored.IsInitiator;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Unreadable: its anchor is reported as not counted
            }
        }

        return new PendingOutputs(pending, funders);
    }

    private static long ToMsat(ulong msat) => msat > long.MaxValue ? long.MaxValue : (long)msat;
}