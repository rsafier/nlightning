using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NetMQ;
using NetMQ.Sockets;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Interfaces;
using Options;

public class BlockchainMonitorService : IBlockchainMonitor
{
    private readonly BitcoinOptions _bitcoinOptions;
    private readonly IBitcoinChainService _bitcoinChainService;
    private readonly ILogger<BlockchainMonitorService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Network _network;
    private readonly SemaphoreSlim _newBlockSemaphore = new(1, 1);
    private readonly SemaphoreSlim _blockBacklogSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<uint256, WatchedTransactionModel> _watchedTransactions = new();
    private readonly ConcurrentDictionary<string, WalletAddressModel> _watchedAddresses = new();
    private readonly ConcurrentDictionary<OutPoint, ChannelId> _watchedOutpoints = new();
    private readonly OrderedDictionary<uint, Block> _blocksToProcess = new();

    private BlockchainState _blockchainState = new(0, Hash.Empty, DateTime.UtcNow);
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private uint _lastProcessedBlockHeight;
    private uint _catchUpHeight;
    private SubscriberSocket? _blockSocket;
    // private SubscriberSocket? _transactionSocket;

    public event EventHandler<NewBlockEventArgs>? OnNewBlockDetected;
    public event EventHandler<TransactionConfirmedEventArgs>? OnTransactionConfirmed;
    public event EventHandler<WalletMovementEventArgs>? OnWalletMovementDetected;
    public event EventHandler<OutpointSpentEventArgs>? OnWatchedOutpointSpent;

    public uint LastProcessedBlockHeight => _lastProcessedBlockHeight;

    /// <summary>
    /// True while the last processing round halted on a block that kept failing (NL-097). Chain events (funding
    /// confirmations, deposits, spends) are not being seen while this is set.
    /// </summary>
    public bool IsChainProcessingHalted { get; private set; }

    /// <summary>
    /// How many times a block is tried in one processing round before the round halts (NL-097).
    /// </summary>
    internal int MaxBlockProcessingAttempts { get; set; } = 3;

    /// <summary>
    /// Delay before the first retry of a failed block; it doubles on every further retry within the round.
    /// </summary>
    internal TimeSpan BlockRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Most blocks held in the processing queue at once. Blocks past this are not kept in memory; they are refetched
    /// from bitcoind once the queue drains, so a halted queue does not grow with every new block.
    /// </summary>
    internal int MaxQueuedBlocks { get; set; } = 144;

    public BlockchainMonitorService(IOptions<BitcoinOptions> bitcoinOptions, IBitcoinChainService bitcoinChainService,
                                    ILogger<BlockchainMonitorService> logger, IOptions<NodeOptions> nodeOptions,
                                    IServiceProvider serviceProvider)
    {
        _bitcoinOptions = bitcoinOptions.Value;
        _bitcoinChainService = bitcoinChainService;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ?? Network.Main;
    }

    public async Task StartAsync(uint heightOfBirth, CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Load pending transactions
        await LoadPendingWatchedTransactionsAsync(uow);

        // Load existing addresses
        LoadBitcoinAddresses(uow);

        // Load UtxoSet
        await LoadUtxoSetAsync(uow);

        // Get the current state or create a new one if it doesn't exist
        var currentBlockchainState = await uow.BlockchainStateDbRepository.GetStateAsync();
        if (currentBlockchainState is null)
        {
            _logger.LogInformation("No blockchain state found, starting from height {Height}", heightOfBirth);

            _lastProcessedBlockHeight = heightOfBirth;
            _blockchainState = new BlockchainState(_lastProcessedBlockHeight, Hash.Empty, DateTime.UtcNow);
            uow.BlockchainStateDbRepository.Add(_blockchainState);
        }
        else
        {
            _blockchainState = currentBlockchainState;
            _lastProcessedBlockHeight = _blockchainState.LastProcessedHeight;
            _logger.LogInformation("Starting blockchain monitoring at height {Height}, last block hash {LastBlockHash}",
                                   _lastProcessedBlockHeight, _blockchainState.LastProcessedBlockHash);
        }

        // Get the current block height from the wallet
        var currentBlockHeight = await _bitcoinChainService.GetCurrentBlockHeightAsync();

        if (currentBlockHeight > _lastProcessedBlockHeight)
        {
            // Add the current block to the processing queue
            var currentBlock = await _bitcoinChainService.GetBlockAsync(_lastProcessedBlockHeight);
            if (currentBlock is not null)
                _blocksToProcess[_lastProcessedBlockHeight] = currentBlock;

            // Add missing blocks to the processing queue and process any pending blocks
            await AddMissingBlocksToProcessAsync(currentBlockHeight);
            await ProcessPendingBlocksAsync(uow);
        }

        await uow.SaveChangesAsync();

        // Initialize ZMQ sockets
        InitializeZmqSockets();

        // Start monitoring task
        _monitoringTask = MonitorBlockchainAsync(_cts.Token);

        _logger.LogInformation("Blockchain monitor service started successfully");
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            throw new InvalidOperationException("Service is not running");

        await _cts.CancelAsync();

        if (_monitoringTask is not null)
        {
            try
            {
                await _monitoringTask;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }

        CleanupZmqSockets();
    }

    public async Task PublishAndWatchTransactionAsync(ChannelId channelId, SignedTransaction signedTransaction,
                                                      uint requiredDepth)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Publishing transaction {TxId} for {RequiredDepth} confirmations for channel {channelId}",
                signedTransaction.TxId, requiredDepth, channelId);

        // Convert the tx
        var transaction = Transaction.Load(signedTransaction.RawTxBytes, _network);

        // Start watching the tx
        await WatchTransactionAsync(channelId, signedTransaction.TxId, requiredDepth);

        // Publish the tx
        await _bitcoinChainService.SendTransactionAsync(transaction);
    }

    public async Task WatchTransactionAsync(ChannelId channelId, TxId txId, uint requiredDepth)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Watching transaction {TxId} for {RequiredDepth} confirmations for channel {channelId}",
                txId, requiredDepth, channelId);

        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var nBitcoinTxId = new uint256(txId);
        var watchedTx = new WatchedTransactionModel(channelId, txId, requiredDepth);

        uow.WatchedTransactionDbRepository.Add(watchedTx);

        _watchedTransactions[nBitcoinTxId] = watchedTx;

        await uow.SaveChangesAsync();
    }

    public void TrackWatchedTransaction(WatchedTransactionModel watchedTransaction)
    {
        ArgumentNullException.ThrowIfNull(watchedTransaction);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Watching transaction {TxId} for {RequiredDepth} confirmations for channel {channelId}",
                watchedTransaction.TransactionId, watchedTransaction.RequiredDepth, watchedTransaction.ChannelId);

        _watchedTransactions[new uint256(watchedTransaction.TransactionId)] = watchedTransaction;
    }

    public async Task PublishTransactionAsync(SignedTransaction signedTransaction)
    {
        ArgumentNullException.ThrowIfNull(signedTransaction);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Publishing transaction {TxId}", signedTransaction.TxId);

        await _bitcoinChainService.SendTransactionAsync(Transaction.Load(signedTransaction.RawTxBytes, _network));
    }

    public void WatchOutpointSpend(ChannelId channelId, TxId txId, uint outputIndex)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Watching outpoint {TxId}:{Index} of channel {ChannelId} for a spend", txId,
                                   outputIndex, channelId);

        _watchedOutpoints[new OutPoint(new uint256(txId), outputIndex)] = channelId;
    }

    public void StopWatchingOutpointSpend(TxId txId, uint outputIndex)
    {
        _watchedOutpoints.TryRemove(new OutPoint(new uint256(txId), outputIndex), out _);
    }

    public void WatchBitcoinAddress(WalletAddressModel walletAddress)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Watching bitcoin address {walletAddress} for deposits", walletAddress);

        _watchedAddresses[walletAddress.Address] = walletAddress;
    }

    // public Task WatchForRevocationAsync(TxId commitmentTxId, SignedTransaction penaltyTx)
    // {
    //     _logger.LogInformation("Watching for revocation of commitment transaction {CommitmentTxId}", commitmentTxId);
    //
    //     var nBitcoinTxId = new uint256(commitmentTxId);
    //     var revocationWatch = new RevocationWatch(nBitcoinTxId, Transaction.Load(penaltyTx.RawTxBytes, _network));
    //
    //     _revocationWatches.TryAdd(nBitcoinTxId, revocationWatch);
    //     return Task.CompletedTask;
    // }

    private async Task MonitorBlockchainAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Starting blockchain monitoring loop");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Check for new blocks
                    if (_blockSocket != null &&
                        _blockSocket.TryReceiveFrameString(TimeSpan.FromMilliseconds(100), out var topic))
                    {
                        if (topic == "rawblock" && _blockSocket.TryReceiveFrameBytes(out var blockHashBytes))
                        {
                            try
                            {
                                // One at a time
                                await _newBlockSemaphore.WaitAsync(cancellationToken);
                                var block = Block.Load(blockHashBytes, _network);
                                var coinbaseHeight = block.GetCoinbaseHeight();
                                if (!coinbaseHeight.HasValue)
                                {
                                    // Get the current height from the wallet
                                    var currentHeight = await _bitcoinChainService.GetCurrentBlockHeightAsync();

                                    // Get the block from the wallet
                                    var blockAtHeight = await _bitcoinChainService.GetBlockAsync(currentHeight);
                                    if (blockAtHeight is null)
                                    {
                                        _logger.LogError("Failed to retrieve block at height {Height}", currentHeight);
                                        return;
                                    }

                                    coinbaseHeight = (int)currentHeight;
                                }

                                await ProcessNewBlock(block, (uint)coinbaseHeight);
                            }
                            finally
                            {
                                _newBlockSemaphore.Release();
                            }
                        }
                    }

                    // TODO: Check for new transactions
                    // if (_transactionSocket != null &&
                    //     _transactionSocket.TryReceiveFrameString(TimeSpan.FromMilliseconds(100), out var txTopic))
                    // {
                    //     if (txTopic == "rawtx" && _transactionSocket.TryReceiveFrameBytes(out var rawTxBytes))
                    //     {
                    //         await ProcessNewTransaction(rawTxBytes);
                    //     }
                    // }

                    // Small delay to prevent CPU spinning
                    await Task.Delay(50, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Error in blockchain monitoring loop");
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Blockchain monitoring loop cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in blockchain monitoring loop");
        }
    }

    private void InitializeZmqSockets()
    {
        try
        {
            // Subscribe to new blocks
            _blockSocket = new SubscriberSocket();
            _blockSocket.Connect($"tcp://{_bitcoinOptions.ZmqHost}:{_bitcoinOptions.ZmqBlockPort}");
            _blockSocket.Subscribe("rawblock");

            // // Subscribe to new transactions (for mempool monitoring)
            // _transactionSocket = new SubscriberSocket();
            // _transactionSocket.Connect($"tcp://{_bitcoinOptions.ZmqHost}:{_bitcoinOptions.ZmqTxPort}");
            // _transactionSocket.Subscribe("rawtx");

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("ZMQ sockets initialized - Block: {BlockPort}, Tx: {TxPort}",
                                       _bitcoinOptions.ZmqBlockPort, _bitcoinOptions.ZmqTxPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ZMQ sockets");
            CleanupZmqSockets();
            throw;
        }
    }

    private void CleanupZmqSockets()
    {
        try
        {
            _blockSocket?.Dispose();
            _blockSocket = null;

            // _transactionSocket?.Dispose();
            // _transactionSocket = null;

            _logger.LogDebug("ZMQ sockets cleaned up");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up ZMQ sockets");
        }
    }

    /// <summary>
    /// Processes the queued blocks in order.
    /// </summary>
    /// <remarks>
    /// Failure policy (NL-097): a block whose processing throws is retried up to
    /// <see cref="MaxBlockProcessingAttempts"/> times with exponential backoff. If it still fails, this round halts:
    /// the failing block and every later block stay queued, nothing is dropped, and the height is not advanced. The
    /// next round (the next ZMQ block, or a restart, which refetches the blocks from bitcoind) starts over from the
    /// failing block. Blocks are never skipped, because a skipped block could hide a funding confirmation, a deposit
    /// or a spend of a watched output.
    /// </remarks>
    private async Task ProcessPendingBlocksAsync(IUnitOfWork uow)
    {
        var cancellationToken = _cts?.Token ?? CancellationToken.None;

        await _blockBacklogSemaphore.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                if (_blocksToProcess.Count == 0)
                {
                    // Refill from bitcoind the blocks that were left out because the queue was full
                    await FillQueueFromChainAsync();
                    if (_blocksToProcess.Count == 0)
                        break;
                }

                var (height, block) = _blocksToProcess.First();
                if (height <= _lastProcessedBlockHeight)
                    _logger.LogWarning("Possible reorg detected: Block {Height} is already processed.", height);

                if (!await TryProcessBlockWithRetriesAsync(block, height, uow, cancellationToken))
                {
                    IsChainProcessingHalted = true;
                    _logger.LogCritical(
                        "Chain processing halted at block {Height} after {Attempts} failed attempts; {Pending} blocks remain queued and will be retried when the next block arrives or on restart",
                        height, MaxBlockProcessingAttempts, _blocksToProcess.Count);
                    return;
                }
            }

            IsChainProcessingHalted = false;
        }
        finally
        {
            _blockBacklogSemaphore.Release();
        }
    }

    private async Task<bool> TryProcessBlockWithRetriesAsync(Block block, uint height, IUnitOfWork uow,
                                                             CancellationToken cancellationToken)
    {
        var delay = BlockRetryBaseDelay;
        for (var attempt = 1; attempt <= MaxBlockProcessingAttempts; attempt++)
        {
            try
            {
                ProcessBlock(block, height, uow);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing block at height {Height} (attempt {Attempt} of {MaxAttempts})",
                                 height, attempt, MaxBlockProcessingAttempts);
            }

            if (attempt < MaxBlockProcessingAttempts && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }

        return false;
    }

    private async Task AddMissingBlocksToProcessAsync(uint currentHeight)
    {
        if (currentHeight > _catchUpHeight)
            _catchUpHeight = currentHeight;

        if (currentHeight > _lastProcessedBlockHeight + 1)
            _logger.LogWarning("Processing missed blocks from height {LastProcessedHeight} to {CurrentHeight}",
                               _lastProcessedBlockHeight + 1, currentHeight);

        await FillQueueFromChainAsync();
    }

    /// <summary>
    /// Fetches the blocks after the last processed one and below <see cref="_catchUpHeight"/> that are not queued yet,
    /// stopping once the queue holds <see cref="MaxQueuedBlocks"/> blocks.
    /// </summary>
    private async Task FillQueueFromChainAsync()
    {
        for (var height = _lastProcessedBlockHeight + 1;
             height < _catchUpHeight && _blocksToProcess.Count < MaxQueuedBlocks;
             height++)
        {
            if (_blocksToProcess.ContainsKey(height))
                continue;

            // Add the missing block to the process queue
            var blockAtHeight = await _bitcoinChainService.GetBlockAsync(height);
            if (blockAtHeight is not null)
            {
                _blocksToProcess[height] = blockAtHeight;
            }
            else
            {
                _logger.LogError("Missing block at height {Height}", height);
            }
        }
    }

    private async Task ProcessNewBlock(Block block, uint currentHeight)
    {
        using var scope = _serviceProvider.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var blockHash = block.GetHash();

        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Processing block at height {blockHeight}: {BlockHash}", currentHeight, blockHash);

            // Check for missed blocks first
            await AddMissingBlocksToProcessAsync(currentHeight);

            // Store the current block for processing, unless the queue is full. Then it is refetched later.
            if (_blocksToProcess.Count < MaxQueuedBlocks)
                _blocksToProcess[currentHeight] = block;
            else if (currentHeight + 1 > _catchUpHeight)
                _catchUpHeight = currentHeight + 1;

            // Process missing blocks
            await ProcessPendingBlocksAsync(uow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing new block {BlockHash}", blockHash);
        }

        await uow.SaveChangesAsync();
    }

    // TODO: Check for revocation transactions in mempool
    // private async Task ProcessNewTransaction(byte[] rawTxBytes)
    // {
    //     try
    //     {
    //         var transaction = Transaction.Load(rawTxBytes, Network.Main);
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Error processing new transaction from mempool");
    //     }
    // }

    /// <summary>
    /// Processes one block and removes it from the queue. Throws if processing fails, leaving the block queued.
    /// </summary>
    private void ProcessBlock(Block block, uint height, IUnitOfWork uow)
    {
        var blockHash = block.GetHash();

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Processing block {Height} with {TxCount} transactions", height,
                             block.Transactions.Count);

        // Notify listeners of the new block
        OnNewBlockDetected?.Invoke(this, new NewBlockEventArgs(height, blockHash.ToBytes()));

        // Check if watched transactions are included in this block
        CheckBlockForWatchedTransactions(block.Transactions, height, uow);

        // Check for deposits in this block
        CheckBlockForWalletMovement(block.Transactions, height, uow);

        // Check for spends of watched outpoints (channel funding outputs)
        CheckBlockForWatchedSpends(block.Transactions, height);

        // Update blockchain state
        _blockchainState.UpdateState(blockHash.ToBytes(), height);
        uow.BlockchainStateDbRepository.Update(_blockchainState);

        _blocksToProcess.Remove(height);

        // Update our internal state
        _lastProcessedBlockHeight = height;

        // Check watched for all transactions' depth
        CheckWatchedTransactionsDepth(uow);
    }

    private void ConfirmTransaction(uint blockHeight, IUnitOfWork uow, WatchedTransactionModel watchedTransaction)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Transaction {TxId} reached required depth of {depth} confirmations at block {blockHeight}",
                watchedTransaction.TransactionId, watchedTransaction.RequiredDepth, blockHeight);

        watchedTransaction.MarkAsCompleted();
        uow.WatchedTransactionDbRepository.Update(watchedTransaction);
        OnTransactionConfirmed?.Invoke(
            this, new TransactionConfirmedEventArgs(watchedTransaction, blockHeight));

        _watchedTransactions.TryRemove(new uint256(watchedTransaction.TransactionId), out _);
    }

    private void CheckBlockForWatchedTransactions(List<Transaction> blockTransactions, uint blockHeight,
                                                  IUnitOfWork uow)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "Checking {watchedTransactionCount} watched transactions for block {height} with {TxCount} transactions",
                _watchedTransactions.Count, blockHeight, blockTransactions.Count);

        // The index must be the position within the block (all txs, coinbase included), as BOLT 7 requires for
        // short_channel_id, not the position among the watched transactions.
        for (var index = 0; index < blockTransactions.Count; index++)
        {
            var transaction = blockTransactions[index];
            var txId = transaction.GetHash();

            if (!_watchedTransactions.TryGetValue(txId, out var watchedTransaction))
                continue;

            _logger.LogInformation("Transaction {TxId} found in block at height {Height}", txId, blockHeight);

            try
            {
                // Update first seen height
                watchedTransaction.SetHeightAndIndex(blockHeight, (uint)index);
                uow.WatchedTransactionDbRepository.Update(watchedTransaction);

                if (watchedTransaction.RequiredDepth == 0)
                    ConfirmTransaction(blockHeight, uow, watchedTransaction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking confirmations for transaction {TxId}", txId);
            }
        }
    }

    /// <summary>
    /// Raises <see cref="OnWatchedOutpointSpent"/> for every transaction of the block that spends a watched outpoint.
    /// The outpoint stays watched: a replayed block raises it again, so listeners must be idempotent.
    /// </summary>
    private void CheckBlockForWatchedSpends(List<Transaction> blockTransactions, uint blockHeight)
    {
        if (_watchedOutpoints.IsEmpty)
            return;

        for (var index = 0; index < blockTransactions.Count; index++)
        {
            var transaction = blockTransactions[index];
            foreach (var input in transaction.Inputs)
            {
                if (!_watchedOutpoints.TryGetValue(input.PrevOut, out var channelId))
                    continue;

                var txId = transaction.GetHash();
                _logger.LogInformation(
                    "Watched outpoint {Outpoint} of channel {ChannelId} spent by {TxId} at height {Height}",
                    input.PrevOut, channelId, txId, blockHeight);
                try
                {
                    var spendingTransaction = new SignedTransaction(txId.ToBytes(), transaction.ToBytes());
                    OnWatchedOutpointSpent?.Invoke(
                        this, new OutpointSpentEventArgs(channelId, spendingTransaction, blockHeight, (uint)index));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling the spend of outpoint {Outpoint}", input.PrevOut);
                }
            }
        }
    }

    private void CheckBlockForWalletMovement(List<Transaction> transactions, uint blockHeight, IUnitOfWork uow)
    {
        if (_watchedAddresses.IsEmpty)
            return;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Checking {AddressCount} watched addresses for deposits/spends in block {Height}",
                             _watchedAddresses.Count, blockHeight);

        foreach (var transaction in transactions)
        {
            var txId = transaction.GetHash();

            // Check each output for deposits
            for (var i = 0; i < transaction.Outputs.Count; i++)
            {
                var output = transaction.Outputs[i];
                var destinationAddress = output.ScriptPubKey.GetDestinationAddress(_network);
                if (destinationAddress == null)
                    continue;

                if (!_watchedAddresses.TryGetValue(destinationAddress.ToString(), out var watchedAddress))
                    continue;

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Deposit detected: {amount} to address {destinationAddress} in tx {txId} at block {height}",
                        output.Value, destinationAddress, txId, blockHeight);

                // A block can be processed again (the last processed block is replayed on start, and a failed block is
                // retried), so a deposit we already track must not be added twice (NL-097).
                var utxoMemoryRepository = _serviceProvider.GetRequiredService<IUtxoMemoryRepository>();
                if (utxoMemoryRepository.TryGetUtxo(new TxId(txId.ToBytes()), (uint)i, out _))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Utxo {TxId}:{Index} is already known, skipping", txId, i);

                    continue;
                }

                // Save Utxo to the database
                var utxo = new UtxoModel(txId.ToBytes(), (uint)i, LightningMoney.Satoshis(output.Value.Satoshi),
                                         blockHeight, watchedAddress);
                uow.AddUtxo(utxo);

                // The address stays watched (as after a restart, which reloads every wallet address): the wallet hands
                // an address out again once its deposits are spent, and two channels closing at once can get the
                // same shutdown address, so a later deposit to it must be found too

                OnWalletMovementDetected
                  ?.Invoke(this, new WalletMovementEventArgs(destinationAddress.ToString(),
                                                             LightningMoney.Satoshis(output.Value.Satoshi),
                                                             txId.ToBytes(),
                                                             blockHeight));
            }

            // Check each input for spent utxos
            foreach (var input in transaction.Inputs)
                uow.TrySpendUtxo(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
        }
    }

    private void CheckWatchedTransactionsDepth(IUnitOfWork uow)
    {
        foreach (var (txId, watchedTransaction) in _watchedTransactions)
        {
            try
            {
                // The FirstSeenAtHeight represents 1 confirmation, so we have to add 1
                var confirmations = _lastProcessedBlockHeight - watchedTransaction.FirstSeenAtHeight + 1;
                if (confirmations >= watchedTransaction.RequiredDepth)
                    ConfirmTransaction(_lastProcessedBlockHeight, uow, watchedTransaction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking confirmations for transaction {TxId}", txId);
            }
        }
    }

    private async Task LoadPendingWatchedTransactionsAsync(IUnitOfWork uow)
    {
        _logger.LogInformation("Loading watched transactions from database");

        var watchedTransactions = await uow.WatchedTransactionDbRepository.GetAllPendingAsync();
        foreach (var watchedTransaction in watchedTransactions)
        {
            _watchedTransactions[new uint256(watchedTransaction.TransactionId)] = watchedTransaction;
        }
    }

    private void LoadBitcoinAddresses(IUnitOfWork uow)
    {
        _logger.LogInformation("Loading bitcoin addresses from database");

        var bitcoinAddresses = uow.WalletAddressesDbRepository.GetAllAddresses();
        foreach (var bitcoinAddress in bitcoinAddresses)
        {
            _watchedAddresses[bitcoinAddress.Address] = bitcoinAddress;
        }
    }

    private async Task LoadUtxoSetAsync(IUnitOfWork uow)
    {
        _logger.LogInformation("Loading Utxo set");

        var utxoSet = (await uow.UtxoDbRepository.GetUnspentAsync()).ToList();
        if (utxoSet.Count > 0)
        {
            var utxoMemoryRepository = _serviceProvider.GetService<IUtxoMemoryRepository>() ??
                                       throw new InvalidOperationException(
                                           $"Error getting required service {nameof(IUtxoMemoryRepository)}");
            utxoMemoryRepository.Load(utxoSet);
        }
    }
}