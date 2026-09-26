using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;

/// <summary>
/// The on-chain wallet reserve of <c>option_anchors</c> channels (NL-379), LND style: <c>Node:Anchors</c>
/// <see cref="AnchorReserveOptions.ReservePerChannel"/> per anchors channel not yet Closed, capped at
/// <see cref="AnchorReserveOptions.MaxReserve"/>. The balance it guards is the confirmed wallet balance of the outputs
/// neither locked to a funding, reserved for a fee nor spent by one of our pending broadcasts (NL-385).
/// </summary>
public sealed class AnchorReserveService : IAnchorReserveService
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<AnchorReserveService> _logger;
    private readonly Network _network;
    private readonly AnchorReserveOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public AnchorReserveService(IUtxoMemoryRepository utxoMemoryRepository,
                                IChannelMemoryRepository channelMemoryRepository,
                                IBlockchainMonitor blockchainMonitor, IServiceScopeFactory scopeFactory,
                                IOptions<NodeOptions> nodeOptions, ILogger<AnchorReserveService> logger)
    {
        _utxoMemoryRepository = utxoMemoryRepository;
        _channelMemoryRepository = channelMemoryRepository;
        _blockchainMonitor = blockchainMonitor;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = nodeOptions.Value.Anchors;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <inheritdoc />
    public int CountAnchorsChannels() =>
        _channelMemoryRepository.FindChannels(c => c.ChannelParams.OptionAnchorOutputs
                                                && c.State is not (ChannelState.Closed or ChannelState.Stale))
                                .Count;

    /// <inheritdoc />
    public LightningMoney GetRequiredReserve(int additionalAnchorsChannels = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalAnchorsChannels);
        return _options.GetRequiredReserve(CountAnchorsChannels() + additionalAnchorsChannels);
    }

    /// <inheritdoc />
    public async Task<AnchorReserveStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var count = CountAnchorsChannels();
        var reserve = _options.GetRequiredReserve(count);
        var available = await GetAvailableBalanceAsync();
        var spendable = available > reserve ? available - reserve : LightningMoney.Zero;
        return new AnchorReserveStatus(count, reserve, available, spendable);
    }

    /// <inheritdoc />
    public async Task EnsureCanAcceptAnchorsChannelAsync(CancellationToken cancellationToken = default)
    {
        var reserve = GetRequiredReserve(1);
        if (reserve.IsZero)
            return;

        var available = await GetAvailableBalanceAsync();
        if (available >= reserve)
            return;

        _logger.LogWarning(
            "Refusing an anchors channel: the confirmed wallet balance {Available} sat does not cover the anchors "
          + "reserve of {Reserve} sat", available.Satoshi, reserve.Satoshi);
        throw new AnchorReserveException(
            $"Not enough confirmed on-chain funds for the anchors reserve: {reserve.Satoshi} sat needed, "
          + $"{available.Satoshi} sat available", reserve, available, reserve);
    }

    /// <inheritdoc />
    public async Task EnsureCanFundAsync(LightningMoney fundingAmount, bool anchorsChannel,
                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fundingAmount);

        var reserve = GetRequiredReserve(anchorsChannel ? 1 : 0);
        var available = await GetAvailableBalanceAsync();
        if (available >= fundingAmount + reserve)
            return;

        if (reserve.IsZero)
            throw new InsufficientFundsException(fundingAmount, available);

        throw new AnchorReserveException(
            $"Funding {fundingAmount.Satoshi} sat would leave the wallet below the anchors reserve of "
          + $"{reserve.Satoshi} sat: {available.Satoshi} sat confirmed and available, "
          + $"{(available > reserve ? available - reserve : LightningMoney.Zero).Satoshi} sat spendable",
            fundingAmount + reserve, available, reserve);
    }

    /// <inheritdoc />
    public async Task<List<UtxoModel>> LockFundingUtxosAsync(LightningMoney fundingAmount, ChannelId channelId,
                                                             bool anchorsChannel,
                                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fundingAmount);

        var reserve = GetRequiredReserve(anchorsChannel ? 1 : 0);
        var excluded = await GetOutpointsSpentByPendingBroadcastsAsync();
        var utxos = _utxoMemoryRepository.LockUtxosToSpendOnChannel(fundingAmount, channelId, reserve, excluded);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "Locked {Count} output(s) for the funding of {ChannelId}, keeping the anchors reserve of {Reserve} sat "
              + "({Excluded} output(s) spent by pending broadcasts skipped)", utxos.Count, channelId, reserve.Satoshi,
                excluded.Count);

        return utxos;
    }

    private async Task<LightningMoney> GetAvailableBalanceAsync()
    {
        var excluded = await GetOutpointsSpentByPendingBroadcastsAsync();
        return _utxoMemoryRepository.GetAvailableConfirmedBalance(_blockchainMonitor.LastProcessedBlockHeight,
                                                                  excluded);
    }

    private async Task<HashSet<(TxId TxId, uint Index)>> GetOutpointsSpentByPendingBroadcastsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
    }
}