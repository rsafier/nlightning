using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="IRevokedCommitDataSource"/> over the database (each call in its own scope, reads only), the chain
/// service (transactions are found in the block that holds them, so no <c>txindex</c> is needed), the wallet and the
/// fee service.
/// </summary>
public sealed class RevokedCommitDataSource : IRevokedCommitDataSource
{
    private const int MaxCachedTransactions = 256;

    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IBitcoinChainService _chainService;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IFeeService _feeService;
    private readonly ILogger<RevokedCommitDataSource> _logger;
    private readonly Network _network;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISecretStorageServiceFactory _secretStorageServiceFactory;
    private readonly ConcurrentDictionary<TxId, ChainTx> _transactions = new();

    public RevokedCommitDataSource(IServiceScopeFactory scopeFactory, IBitcoinChainService chainService,
                                   ISecretStorageServiceFactory secretStorageServiceFactory, IFeeService feeService,
                                   IOptions<NodeOptions> nodeOptions, ILogger<RevokedCommitDataSource> logger,
                                   IChannelMemoryRepository? channelMemoryRepository = null,
                                   IBlockchainMonitor? blockchainMonitor = null)
    {
        _scopeFactory = scopeFactory;
        _chainService = chainService;
        _secretStorageServiceFactory = secretStorageServiceFactory;
        _feeService = feeService;
        _logger = logger;
        _channelMemoryRepository = channelMemoryRepository;
        _blockchainMonitor = blockchainMonitor;
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ??
                   throw new ArgumentException("Invalid Bitcoin network specified", nameof(nodeOptions));
    }

    /// <inheritdoc />
    public Task<RevokedCommitLoadResult> LoadAsync(ChannelCloseModel close, CancellationToken cancellationToken) =>
        LoadCoreAsync(close, null);

    /// <inheritdoc />
    public Task<RevokedCommitLoadResult> LoadAsync(ChannelCloseModel close, ChainTx commitment,
                                                   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitment);
        return LoadCoreAsync(close, commitment);
    }

    private async Task<RevokedCommitLoadResult> LoadCoreAsync(ChannelCloseModel close, ChainTx? unconfirmed)
    {
        ArgumentNullException.ThrowIfNull(close);
        if (close.CommitmentNumber is not { } number)
            return RevokedCommitLoadResult.Missing("the close record has no commitment number");

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        ChannelModel? channel = null;
        if (_channelMemoryRepository?.TryGetChannel(close.ChannelId, out var inMemory) == true)
            channel = inMemory;
        channel ??= await unitOfWork.ChannelDbRepository.GetByIdAsync(close.ChannelId);
        if (channel?.RemoteKeySet is null)
            return RevokedCommitLoadResult.Missing("the channel or its peer keys are unknown");

        Secret secret;
        var entries = await unitOfWork.RemoteShachainDbRepository.GetByChannelIdAsync(close.ChannelId);
        using (var shachain = _secretStorageServiceFactory.CreatePerCommitmentStorage())
        {
            try
            {
                shachain.Load(entries);
                secret = shachain.DeriveOldSecret(PerCommitmentIndex.From(number));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                return RevokedCommitLoadResult.Missing($"the peer's shachain does not hold secret {number}: {e.Message}");
            }
        }

        CompactPubKey point;
        using (var key = new Key((byte[])secret))
            point = key.PubKey.ToBytes();

        var logEntry = await unitOfWork.RevokedCommitmentDbRepository.GetAsync(close.ChannelId, number);
        var logStart = await unitOfWork.RevokedCommitmentDbRepository.GetLogStartAsync(close.ChannelId);

        var commitment = unconfirmed ?? await GetTransactionAsync(close.CommitmentTransactionId, close.SpentAtHeight);
        if (commitment is null)
            return RevokedCommitLoadResult.Missing(
                $"transaction {close.CommitmentTransactionId} is not in block {close.SpentAtHeight}");

        return RevokedCommitLoadResult.Found(new RevokedCommitContext(channel, commitment, number, secret, point,
                                                                      logEntry, logStart));
    }

    /// <inheritdoc />
    public async Task<RevokedOutputSpend?> GetSpendAsync(TxId transactionId, uint outputIndex,
                                                         CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(transactionId, outputIndex);
        if (watch is not { SpentByTransactionId: { } spender, SpentAtHeight: { } height })
            return null;

        // A spender that cannot be fetched is still a spend: its txid alone says whether it is ours
        var transaction = await GetTransactionAsync(spender, height);
        var byUs = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(spender) is not null;
        return new RevokedOutputSpend(spender, transaction, height, byUs);
    }

    /// <inheritdoc />
    public async Task<bool> IsOurTransactionAsync(TxId transactionId) =>
        await GetBroadcastAsync(transactionId) is not null;

    /// <inheritdoc />
    public async Task<BroadcastTransactionModel?> GetBroadcastAsync(TxId transactionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(transactionId);
    }

    /// <inheritdoc />
    public async Task<byte[]> GetDestinationScriptAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var walletService = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = await walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, false);

        // The monitor credits the penalty output to the wallet once it confirms (idempotent)
        _blockchainMonitor?.WatchBitcoinAddress(address);
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes();
    }

    /// <inheritdoc />
    public async Task<uint> GetFeeratePerKwAsync(uint confirmationTarget, CancellationToken cancellationToken)
    {
        var estimate = await Fees.FeeEstimates.GetForTargetAsync(_feeService, confirmationTarget, _logger,
                                                                 cancellationToken);
        return estimate > 0 ? estimate : await GetFeeratePerKwAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<uint> GetFeeratePerKwAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rate = await _feeService.GetFeeRatePerKwAsync(cancellationToken);
            if (rate.Satoshi > 0)
                return (uint)Math.Min(uint.MaxValue, rate.Satoshi);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Fee estimate failed; using the cached rate for penalties");
        }

        return (uint)Math.Clamp(_feeService.GetCachedFeeRatePerKw().Satoshi, 0, uint.MaxValue);
    }

    private async Task<ChainTx?> GetTransactionAsync(TxId transactionId, uint height)
    {
        if (_transactions.TryGetValue(transactionId, out var cached))
            return cached;

        Transaction? found = null;
        var hash = new uint256((byte[])transactionId);
        try
        {
            var block = await _chainService.GetBlockAsync(height);
            found = block?.Transactions.FirstOrDefault(t => t.GetHash() == hash);
            found ??= await _chainService.GetTransactionAsync(hash);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Transaction {TxId} at height {Height} could not be fetched", transactionId, height);
        }

        if (found is null)
            return null;

        var chainTx = ChainTxMapper.FromTransaction(found);
        if (_transactions.Count >= MaxCachedTransactions)
            _transactions.Clear();
        _transactions[transactionId] = chainTx;
        return chainTx;
    }
}