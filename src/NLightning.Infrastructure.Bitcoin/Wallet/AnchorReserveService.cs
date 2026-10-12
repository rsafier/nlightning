using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;

/// <summary>
/// The on-chain wallet reserve of <c>option_anchors</c> channels (NL-379), LND style: <c>Node:Anchors</c>
/// <see cref="AnchorReserveOptions.ReservePerChannel"/> per anchors channel not yet Closed (pending opens included),
/// capped at <see cref="AnchorReserveOptions.MaxReserve"/>. The balance it guards is the confirmed wallet balance the fee
/// input selector could spend, of the outputs neither locked to a funding, reserved for a fee nor spent by one of our
/// pending broadcasts (NL-385).
/// </summary>
public sealed class AnchorReserveService : IAnchorReserveService
{
    /// <summary>
    /// How long an admitted open counts while its temporary channel is not (or no longer) in the channel memory
    /// repository: covers the time between the check and the store, and a channel upgraded before we looked.
    /// </summary>
    internal static readonly TimeSpan StoreGrace = TimeSpan.FromSeconds(30);

    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<AnchorReserveService> _logger;
    private readonly Network _network;
    private readonly AnchorReserveOptions _options;
    private readonly Dictionary<ChannelId, PendingOpen> _pendingOpens = [];
    private readonly Lock _pendingLock = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public AnchorReserveService(IUtxoMemoryRepository utxoMemoryRepository,
                                IChannelMemoryRepository channelMemoryRepository,
                                IBlockchainMonitor blockchainMonitor, IServiceScopeFactory scopeFactory,
                                IOptions<NodeOptions> nodeOptions, ILogger<AnchorReserveService> logger,
                                TimeProvider? timeProvider = null)
    {
        _utxoMemoryRepository = utxoMemoryRepository;
        _channelMemoryRepository = channelMemoryRepository;
        _blockchainMonitor = blockchainMonitor;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = nodeOptions.Value.Anchors;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <inheritdoc />
    public int CountAnchorsChannels() => CountAnchorsChannels(null);

    /// <inheritdoc />
    public LightningMoney GetRequiredReserve(int additionalAnchorsChannels = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalAnchorsChannels);
        return _options.GetRequiredReserve(CountAnchorsChannels() + additionalAnchorsChannels);
    }

    /// <inheritdoc />
    public async Task<AnchorReserveStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // Under the admission gate: a caller that reserved outputs and then checks the reserve (withdraw) either runs
        // before an accept's check (which then no longer counts those outputs) or after its admission (and counts the
        // channel), never in between
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var count = CountAnchorsChannels();
            var reserve = _options.GetRequiredReserve(count);
            var available = LightningMoney.Satoshis((await GetReserveBackingOutputsAsync())
                                                       .Sum(u => u.Amount.Satoshi));
            var spendable = available > reserve ? available - reserve : LightningMoney.Zero;
            return new AnchorReserveStatus(count, reserve, available, spendable);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsureCanAcceptAnchorsChannelAsync(ChannelModel channel,
                                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Check and admit under one gate, so a concurrent accept or open sees this channel counted
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var reserve = GetReserveFor(channel.ChannelId, true);
            if (reserve.IsZero)
                return;

            var available = LightningMoney.Satoshis((await GetReserveBackingOutputsAsync())
                                                       .Sum(u => u.Amount.Satoshi));
            if (available < reserve)
            {
                _logger.LogWarning(
                    "Refusing anchors channel {ChannelId}: the confirmed wallet balance {Available} sat does not cover "
                  + "the anchors reserve of {Reserve} sat", channel.ChannelId, available.Satoshi, reserve.Satoshi);
                throw new AnchorReserveException(
                    $"Not enough confirmed on-chain funds for the anchors reserve: {reserve.Satoshi} sat needed, "
                  + $"{available.Satoshi} sat available", reserve, available, reserve);
            }

            AddPendingOpen(channel);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task EnsureCanFundAsync(LightningMoney fundingAmount, ChannelModel channel,
                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fundingAmount);
        ArgumentNullException.ThrowIfNull(channel);

        var anchorsChannel = channel.ChannelParams.OptionAnchorOutputs;
        var feeRatePerKw = channel.ChannelParams.FeeRateAmountPerKw;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var reserve = GetReserveFor(channel.ChannelId, anchorsChannel);

            // The largest confirmed outputs first, as many as the amount and their own worst-case fee need: the
            // funding's fee comes out of the wallet too (FundingTransactionModelFactory)
            var outputs = (await GetReserveBackingOutputsAsync()).OrderByDescending(u => u.Amount.Satoshi).ToList();
            var available = LightningMoney.Satoshis(outputs.Sum(u => u.Amount.Satoshi));
            var fee = EstimateFundingFee(outputs, fundingAmount, feeRatePerKw);
            var required = fundingAmount + fee + reserve;
            if (available < required)
            {
                if (reserve.IsZero)
                    throw new InsufficientFundsException(fundingAmount + fee, available);

                var spendable = available > reserve ? available - reserve : LightningMoney.Zero;
                throw new AnchorReserveException(
                    $"Funding {fundingAmount.Satoshi} sat (fee up to {fee.Satoshi} sat) would leave the wallet below "
                  + $"the anchors reserve of {reserve.Satoshi} sat: {available.Satoshi} sat confirmed and available, "
                  + $"{spendable.Satoshi} sat spendable", required, available, reserve);
            }

            if (anchorsChannel)
                AddPendingOpen(channel);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<List<UtxoModel>> LockFundingUtxosAsync(LightningMoney fundingAmount, ChannelModel channel,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fundingAmount);
        ArgumentNullException.ThrowIfNull(channel);

        // The repository checks the reserve atomically with the lock; the reserve counts every other admitted open
        var reserve = GetReserveFor(channel.ChannelId, channel.ChannelParams.OptionAnchorOutputs);
        var excluded = await GetOutpointsSpentByPendingBroadcastsAsync();
        var utxos = _utxoMemoryRepository.LockUtxosToSpendOnChannel(fundingAmount, channel.ChannelId, reserve,
                                                                    excluded,
                                                                    channel.ChannelParams.FeeRateAmountPerKw,
                                                                    _blockchainMonitor.LastProcessedBlockHeight);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "Locked {Count} output(s) for the funding of {ChannelId}, keeping the anchors reserve of {Reserve} sat "
              + "({Excluded} output(s) spent by pending broadcasts skipped)", utxos.Count, channel.ChannelId,
                reserve.Satoshi, excluded.Count);

        return utxos;
    }

    /// <inheritdoc />
    public void ReleasePendingChannel(ChannelId temporaryChannelId)
    {
        lock (_pendingLock)
            _pendingOpens.Remove(temporaryChannelId);
    }

    /// <summary>
    /// The worst-case fee of a funding of <paramref name="fundingAmount"/> from the largest of
    /// <paramref name="outputsLargestFirst"/>: P2WPKH inputs, as many as the amount and their fee need (at least one),
    /// and a P2TR change output.
    /// </summary>
    internal static LightningMoney EstimateFundingFee(IReadOnlyList<UtxoModel> outputsLargestFirst,
                                                      LightningMoney fundingAmount, LightningMoney feeRatePerKw)
    {
        var inputCount = 1;
        long totalSat = 0;
        for (var i = 0; i < outputsLargestFirst.Count; i++)
        {
            totalSat += outputsLargestFirst[i].Amount.Satoshi;
            inputCount = i + 1;
            var needed = fundingAmount + FundingFeeEstimator.EstimateWorstCaseFee(inputCount, feeRatePerKw);
            if (LightningMoney.Satoshis(totalSat) >= needed)
                break;
        }

        return FundingFeeEstimator.EstimateWorstCaseFee(inputCount, feeRatePerKw);
    }

    private LightningMoney GetReserveFor(ChannelId temporaryChannelId, bool anchorsChannel) =>
        _options.GetRequiredReserve(CountAnchorsChannels(temporaryChannelId) + (anchorsChannel ? 1 : 0));

    private int CountAnchorsChannels(ChannelId? exceptPendingOpen)
    {
        var channels = _channelMemoryRepository.FindChannels(c => c.ChannelParams.OptionAnchorOutputs
                                                               && c.State is not (ChannelState.Closed
                                                                                  or ChannelState.Stale))
                                               .Count;
        return channels + CountPendingOpens(exceptPendingOpen);
    }

    private void AddPendingOpen(ChannelModel channel)
    {
        lock (_pendingLock)
            _pendingOpens[channel.ChannelId] = new PendingOpen(channel, channel.RemoteNodeId, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Admitted anchors opens not yet funded: counted while their temporary channel is stored (up to
    /// <see cref="AnchorReserveOptions.PendingOpenTimeout"/>) or, before it is stored, for <see cref="StoreGrace"/>.
    /// A funded open (its model upgraded to the real channel id and stored as a channel) counts as a channel instead;
    /// a dropped one stops counting once it is no longer stored.
    /// </summary>
    private int CountPendingOpens(ChannelId? except)
    {
        var now = _timeProvider.GetUtcNow();
        var count = 0;
        lock (_pendingLock)
        {
            foreach (var (channelId, pending) in _pendingOpens.ToList())
            {
                // Funded: the model took its real channel id and is a channel now (counted as one)
                if (!pending.Channel.ChannelId.Equals(channelId)
                 && _channelMemoryRepository.TryGetChannel(pending.Channel.ChannelId, out _))
                {
                    _pendingOpens.Remove(channelId);
                    continue;
                }

                var age = now - pending.AdmittedAt;
                var stored = _channelMemoryRepository.TryGetTemporaryChannelState(pending.PeerPubKey, channelId, out _);
                if (age >= _options.PendingOpenTimeout || (!stored && age >= StoreGrace))
                {
                    _pendingOpens.Remove(channelId);
                    continue;
                }

                if (except is not { } skipped || !channelId.Equals(skipped))
                    count++;
            }
        }

        return count;
    }

    private async Task<List<UtxoModel>> GetReserveBackingOutputsAsync()
    {
        var excluded = await GetOutpointsSpentByPendingBroadcastsAsync();
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        return _utxoMemoryRepository.GetUnreservedUtxos()
                                    .Where(u => u.BacksAnchorReserve(height) && !excluded.Contains((u.TxId, u.Index)))
                                    .ToList();
    }

    private async Task<HashSet<(TxId TxId, uint Index)>> GetOutpointsSpentByPendingBroadcastsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
    }

    private sealed record PendingOpen(ChannelModel Channel, CompactPubKey PeerPubKey, DateTimeOffset AdmittedAt);
}