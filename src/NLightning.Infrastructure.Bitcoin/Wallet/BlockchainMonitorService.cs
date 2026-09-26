using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
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
using Domain.Onchain.Enums;
using Domain.Onchain.Events;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;
using Options;

/// <summary>
/// Follows the chain: ZMQ <c>rawblock</c> for new blocks, RPC to catch up, one unit of work per block.
/// </summary>
/// <remarks>
/// <para>Per block (BOLT 5 plan O0): watched transactions (first sighting and depth), wallet deposits and spends,
/// spends of watched outpoints, confirmations of the transactions we broadcast, the blockchain state and the header
/// ring are staged in one unit of work and saved together (NL-214). Memory is updated and the events are raised only
/// after that save; a failed attempt leaves nothing behind and is retried (NL-097).</para>
/// <para>Reorgs (NL-096): the hashes of the last <see cref="HeaderRingSize"/> processed blocks are kept. A block whose
/// hash differs from the stored one at its height, or whose parent is not the stored tip, starts a rewind: the fork
/// point is found by asking bitcoind for the active chain's hashes, the rows the disconnected blocks changed are
/// rolled back in one save (first-seen heights of pending watches, outpoint spends, broadcast confirmations, headers,
/// state), <see cref="OnBlockDisconnected"/> is raised per disconnected block, and the new branch is processed. The fork
/// is searched from our last processed block down; a block that is not in the active chain while every block we
/// processed still is (a late notification of an orphan) is dropped without a rewind. A reorg deeper than the ring
/// halts processing (Critical). A watch that completed in a disconnected block is pending again (NL-292: its confirmation
/// is raised again from the new branch, so a funding transaction's short channel id follows it); wallet deposits of the
/// disconnected blocks are removed and wallet outputs they spent are restored when bitcoind reports them unspent
/// (NL-293).</para>
/// <para>Broadcasts (NL-258): every stored <see cref="BroadcastState.Pending"/> transaction is sent again after each
/// processing round (also a halted one) and at startup (also when halted), until a processed block holds it.</para>
/// <para>Mempool (BOLT 5 plan O8, NL-098): with <see cref="BitcoinOptions.WatchMempool"/> a second loop reads ZMQ
/// <c>rawtx</c> and raises <see cref="OnWatchedOutpointSpentInMempool"/> for a transaction that spends a watched
/// outpoint, or an output of a transaction it reported before. Nothing is saved or marked spent for it: only a
/// processed block confirms a spend.</para>
/// </remarks>
public class BlockchainMonitorService : IBlockchainMonitor
{
    // bitcoind rejections that mean it already has the transaction (mempool or chain)
    private static readonly string[] s_alreadyKnownRejections =
    [
        "txn-already-in-mempool", "txn-already-known", "txn-same-nonwitness-data-in-mempool",
        "already in block chain"
    ];

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
    private readonly ConcurrentDictionary<uint256, BroadcastTransactionModel> _pendingBroadcasts = new();
    private readonly ConcurrentDictionary<uint256, int> _refusals = new();
    private readonly SortedDictionary<uint, BlockHeaderModel> _headers = new();
    private readonly OrderedDictionary<uint, Block> _blocksToProcess = new();
    private readonly Lock _mempoolLock = new();
    private readonly HashSet<uint256> _seenMempoolTransactions = [];
    private readonly Queue<uint256> _seenMempoolOrder = new();
    private readonly Dictionary<uint256, ChannelId> _reportedMempoolParents = [];
    private readonly Queue<uint256> _reportedMempoolOrder = new();

    private BlockchainState _blockchainState = new(0, Hash.Empty, DateTime.UtcNow);
    private CancellationTokenSource? _cts;
    private Task? _monitoringTask;
    private Task? _mempoolTask;
    private uint _lastProcessedBlockHeight;
    private uint _catchUpHeight;
    private SubscriberSocket? _blockSocket;
    private SubscriberSocket? _txSocket;

    public event EventHandler<NewBlockEventArgs>? OnNewBlockDetected;
    public event EventHandler<TransactionConfirmedEventArgs>? OnTransactionConfirmed;
    public event EventHandler<WalletMovementEventArgs>? OnWalletMovementDetected;
    public event EventHandler<OutpointSpentEventArgs>? OnWatchedOutpointSpent;
    public event EventHandler<BlockDisconnectedEventArgs>? OnBlockDisconnected;
    public event EventHandler<MempoolSpendEventArgs>? OnWatchedOutpointSpentInMempool;

    public uint LastProcessedBlockHeight => _lastProcessedBlockHeight;

    /// <inheritdoc />
    public bool IsChainProcessingHalted { get; private set; }

    /// <inheritdoc />
    public string? ChainProcessingHaltReason { get; private set; }

    /// <summary>
    /// How many mempool txids are remembered to raise each transaction once (ZMQ <c>rawtx</c> announces a transaction on
    /// mempool acceptance and again when a block holding it is connected), and how many reported transactions are
    /// remembered as parents whose outputs are followed too. The oldest are forgotten first.
    /// </summary>
    internal int MaxRememberedMempoolTransactions { get; set; } = 10_000;

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

    /// <summary>
    /// How many processed block headers are kept for reorg detection; a deeper reorg halts processing.
    /// </summary>
    internal int HeaderRingSize { get; set; } = 100;

    /// <summary>
    /// A pending broadcast the node keeps refusing is logged at Warning on its first refusal and then once every this
    /// many refusals in a row (it is retried after every block, with no abandonment rule yet).
    /// </summary>
    internal int RefusalWarningInterval { get; set; } = 6;

    public BlockchainMonitorService(IOptions<BitcoinOptions> bitcoinOptions, IBitcoinChainService bitcoinChainService,
                                    ILogger<BlockchainMonitorService> logger, IOptions<NodeOptions> nodeOptions,
                                    IServiceProvider serviceProvider)
    {
        _bitcoinOptions = bitcoinOptions.Value;
        _bitcoinChainService = bitcoinChainService;
        _logger = logger;
        _serviceProvider = serviceProvider;
        // Fails on an unknown network; signet and custom signets (Mutinynet) map to NBitcoin's signet (W4-D)
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    public async Task StartAsync(uint heightOfBirth, CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using (var scope = _serviceProvider.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await LoadPendingWatchedTransactionsAsync(uow);
            LoadBitcoinAddresses(uow);
            await LoadUtxoSetAsync(uow);

            // Every channel past funding_created that is not closed gets its funding output watched (backfill for
            // channels stored before the watch existed, or whose watch was never saved)
            var backfilled = await uow.WatchedOutpointDbRepository.AddMissingFundingOutpointsAsync();
            if (backfilled.Count > 0 && _logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Watching the funding outputs of {Count} channels that had no watch",
                                       backfilled.Count);

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

            // The state and the backfilled watches are saved before any block is processed (each block updates them
            // in a unit of work of its own)
            await uow.SaveChangesAsync();

            foreach (var watchedOutpoint in await uow.WatchedOutpointDbRepository.GetActiveAsync())
                TrackWatchedOutpoint(watchedOutpoint);

            foreach (var header in await uow.BlockHeaderDbRepository.GetAllAsync())
                _headers[header.Height] = header;

            foreach (var broadcast in await uow.BroadcastTransactionDbRepository.GetPendingAsync())
                _pendingBroadcasts[new uint256(broadcast.TransactionId)] = broadcast;
        }

        var currentBlockHeight = await _bitcoinChainService.GetCurrentBlockHeightAsync();
        if (currentBlockHeight < _lastProcessedBlockHeight && _headers.Count > 0)
        {
            // The chain is shorter than what we processed (reorg or invalidateblock while we were down)
            _logger.LogWarning("The chain tip {Tip} is below our last processed block {Height}; rewinding",
                               currentBlockHeight, _lastProcessedBlockHeight);
            if (!await TryRewindAsync(currentBlockHeight))
                Halt($"the chain tip {currentBlockHeight} is below our last processed block "
                   + $"{_lastProcessedBlockHeight} and no fork point was found in the header ring");
        }
        else if (currentBlockHeight >= _lastProcessedBlockHeight)
        {
            // The last processed block is processed again (a new state's first block was never processed, and a
            // changed hash at that height is a reorg); then every block up to and including the tip (NL-215)
            var lastBlock = await _bitcoinChainService.GetBlockAsync(_lastProcessedBlockHeight);
            if (lastBlock is not null)
                _blocksToProcess[_lastProcessedBlockHeight] = lastBlock;

            await AddMissingBlocksToProcessAsync(currentBlockHeight + 1);
        }

        if (!IsChainProcessingHalted)
            await ProcessPendingBlocksAsync();
        else
            await RebroadcastPendingAsync(); // A halt must not keep our pending transactions off the chain

        // Initialize ZMQ sockets
        InitializeZmqSockets();

        // Start monitoring task
        _monitoringTask = MonitorBlockchainAsync(_cts.Token);
        if (_txSocket is not null)
        {
            var mempoolToken = _cts.Token;
            _mempoolTask = Task.Factory.StartNew(() => MonitorMempool(mempoolToken), mempoolToken,
                                                 TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        _logger.LogInformation("Blockchain monitor service started successfully");
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            throw new InvalidOperationException("Service is not running");

        await _cts.CancelAsync();

        foreach (var task in new[] { _monitoringTask, _mempoolTask })
        {
            if (task is null)
                continue;

            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected during cancellation
            }
        }

        CleanupZmqSockets();
    }

    /// <inheritdoc />
    public async Task PublishAndWatchTransactionAsync(ChannelId channelId, SignedTransaction signedTransaction,
                                                      uint requiredDepth)
    {
        ArgumentNullException.ThrowIfNull(signedTransaction);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Publishing transaction {TxId} for {RequiredDepth} confirmations for channel {channelId}",
                signedTransaction.TxId, requiredDepth, channelId);

        // Convert the tx first: nothing is stored for bytes that are not a transaction
        var transaction = Transaction.Load(signedTransaction.RawTxBytes, _network);

        // The watch and the raw transaction are saved together, before the send: a failed send is sent again after
        // every block (NL-258)
        var watchedTx = new WatchedTransactionModel(channelId, signedTransaction.TxId, requiredDepth);
        var broadcast = new BroadcastTransactionModel(signedTransaction, BroadcastPurpose.Unspecified, channelId,
                                                      _lastProcessedBlockHeight);
        using (var scope = _serviceProvider.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            uow.WatchedTransactionDbRepository.Add(watchedTx);
            if (await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(signedTransaction.TxId) is null)
                uow.BroadcastTransactionDbRepository.Add(broadcast);
            await uow.SaveChangesAsync();
        }

        _watchedTransactions[new uint256(signedTransaction.TxId)] = watchedTx;
        _pendingBroadcasts[new uint256(signedTransaction.TxId)] = broadcast;

        // Publish the tx; a refusal is the caller's to handle, the stored row keeps it for rebroadcast
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

        await uow.SaveChangesAsync();

        _watchedTransactions[nBitcoinTxId] = watchedTx;
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

    /// <inheritdoc />
    public async Task<bool> PublishAsync(BroadcastTransactionModel transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var txId = new uint256(transaction.TransactionId);
        if (transaction.State == BroadcastState.Pending)
            _pendingBroadcasts[txId] = transaction;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Publishing {Purpose} transaction {TxId}", Enum.GetName(transaction.Purpose), txId);

        return await TrySendAsync(transaction);
    }

    /// <inheritdoc />
    public async Task<bool> SaveAndPublishAsync(BroadcastTransactionModel transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // Nothing is stored for bytes that are not a transaction
        _ = Transaction.Load(transaction.RawTransaction, _network);

        BroadcastTransactionModel stored;
        using (var scope = _serviceProvider.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var existing = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(transaction.TransactionId);
            if (existing is null)
            {
                uow.BroadcastTransactionDbRepository.Add(transaction);
                await uow.SaveChangesAsync();
            }

            stored = existing ?? transaction;
        }

        return await PublishAsync(stored);
    }

    /// <inheritdoc />
    public async Task WatchOutpointAsync(WatchedOutpointModel watchedOutpoint)
    {
        ArgumentNullException.ThrowIfNull(watchedOutpoint);
        using (var scope = _serviceProvider.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (await uow.WatchedOutpointDbRepository.GetAsync(watchedOutpoint.TransactionId,
                                                               watchedOutpoint.OutputIndex) is null)
            {
                uow.WatchedOutpointDbRepository.Add(watchedOutpoint);
                await uow.SaveChangesAsync();
            }
        }

        TrackWatchedOutpoint(watchedOutpoint);
    }

    /// <inheritdoc />
    public void TrackWatchedOutpoint(WatchedOutpointModel watchedOutpoint)
    {
        ArgumentNullException.ThrowIfNull(watchedOutpoint);
        WatchOutpointSpend(watchedOutpoint.ChannelId, watchedOutpoint.TransactionId, watchedOutpoint.OutputIndex);
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

    /// <summary>
    /// A block announced by ZMQ: queues it (with any block missed before it) and processes the queue.
    /// </summary>
    /// <remarks>
    /// Callers are serialized (NL-310): the queue and the catch-up height are touched by one caller at a time, so a
    /// block handed in directly never interleaves with one the ZMQ loop delivers. A block above bitcoind's tip is not
    /// from the node we follow (e.g. a ZMQ endpoint of another chain on the same port) and is dropped: queuing it would
    /// raise the catch-up height to heights the node does not have.
    /// </remarks>
    internal async Task ProcessNewBlockAsync(Block block, uint currentHeight)
    {
        var blockHash = block.GetHash();
        await _newBlockSemaphore.WaitAsync();
        try
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Processing block at height {blockHeight}: {BlockHash}", currentHeight, blockHash);

            var tipHeight = await _bitcoinChainService.GetCurrentBlockHeightAsync();
            if (currentHeight > tipHeight)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "Dropping block {BlockHash} at height {Height}: above the node's tip {Tip}, so not from the chain we follow",
                        blockHash, currentHeight, tipHeight);
                return;
            }

            // Check for missed blocks first
            await AddMissingBlocksToProcessAsync(currentHeight);

            // Store the current block for processing, unless the queue is full. Then it is refetched later.
            if (_blocksToProcess.Count < MaxQueuedBlocks)
                _blocksToProcess[currentHeight] = block;
            else if (currentHeight + 1 > _catchUpHeight)
                _catchUpHeight = currentHeight + 1;

            await ProcessPendingBlocksAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing new block {BlockHash}", blockHash);
        }
        finally
        {
            _newBlockSemaphore.Release();
        }
    }

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
                            var block = Block.Load(blockHashBytes, _network);
                            var coinbaseHeight = block.GetCoinbaseHeight();
                            if (!coinbaseHeight.HasValue)
                            {
                                // Get the current height from the wallet
                                var currentHeight = await _bitcoinChainService.GetCurrentBlockHeightAsync();
                                coinbaseHeight = (int)currentHeight;
                            }

                            // One at a time: ProcessNewBlockAsync serializes its callers
                            await ProcessNewBlockAsync(block, (uint)coinbaseHeight);
                        }
                    }

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

            // BOLT 5 plan O8: unconfirmed spends of watched outputs (optional; blocks alone are enough)
            if (_bitcoinOptions.WatchMempool)
            {
                _txSocket = new SubscriberSocket();
                _txSocket.Connect($"tcp://{_bitcoinOptions.ZmqHost}:{_bitcoinOptions.ZmqTxPort}");
                _txSocket.Subscribe("rawtx");
            }

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("ZMQ sockets initialized - Block: {BlockPort}, Tx: {TxPort} (mempool {Mempool})",
                                       _bitcoinOptions.ZmqBlockPort, _bitcoinOptions.ZmqTxPort,
                                       _bitcoinOptions.WatchMempool ? "watched" : "not watched");
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
            _txSocket?.Dispose();
            _txSocket = null;

            _logger.LogDebug("ZMQ sockets cleaned up");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up ZMQ sockets");
        }
    }

    /// <summary>
    /// A transaction announced by ZMQ <c>rawtx</c> (BOLT 5 plan O8): raises
    /// <see cref="OnWatchedOutpointSpentInMempool"/> once per spent watched outpoint, and once per spent output of a
    /// transaction reported before (so an HTLC transaction that spends a commitment still in the mempool is seen too).
    /// </summary>
    /// <remarks>
    /// Nothing is saved and no watch is marked spent: an unconfirmed transaction may be replaced, evicted or never
    /// mined, and only a processed block confirms a spend. A transaction is handled once (bitcoind announces it again
    /// when a block holding it is connected); txids and reported parents are remembered up to
    /// <see cref="MaxRememberedMempoolTransactions"/> each, the oldest forgotten first. Returns the number of events
    /// raised.
    /// </remarks>
    internal int ProcessMempoolTransaction(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var txId = transaction.GetHash();
        var spends = new List<MempoolSpendEventArgs>();
        lock (_mempoolLock)
        {
            if (!Remember(_seenMempoolTransactions, _seenMempoolOrder, txId))
                return 0;

            SignedTransaction? signed = null;
            foreach (var input in transaction.Inputs)
            {
                var spendsParent = false;
                if (!_watchedOutpoints.TryGetValue(input.PrevOut, out var channelId))
                {
                    if (!_reportedMempoolParents.TryGetValue(input.PrevOut.Hash, out channelId))
                        continue;

                    spendsParent = true;
                }

                signed ??= new SignedTransaction(new TxId(txId.ToBytes()), transaction.ToBytes());
                spends.Add(new MempoolSpendEventArgs(channelId, signed, new TxId(input.PrevOut.Hash.ToBytes()),
                                                     input.PrevOut.N, spendsParent));
            }

            if (spends.Count == 0)
                return 0;

            if (_reportedMempoolParents.TryAdd(txId, spends[0].ChannelId))
            {
                _reportedMempoolOrder.Enqueue(txId);
                while (_reportedMempoolOrder.Count > MaxRememberedMempoolTransactions)
                    _reportedMempoolParents.Remove(_reportedMempoolOrder.Dequeue());
            }
        }

        foreach (var spend in spends)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "Unconfirmed transaction {TxId} spends {SpentTxId}:{Index} of channel {ChannelId}{Parent}", txId,
                    new uint256((byte[])spend.SpentTransactionId), spend.SpentOutputIndex, spend.ChannelId,
                    spend.SpendsUnconfirmedParent ? " (an output of an unconfirmed transaction)" : string.Empty);

            // Each handler on its own: one that throws does not keep the others from the spend
            if (OnWatchedOutpointSpentInMempool is not { } handlers)
                continue;
            foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<MempoolSpendEventArgs>>())
                Raise(() => handler(this, spend), "mempool spend");
        }

        return spends.Count;
    }

    /// <summary>Adds <paramref name="txId"/> to a bounded set; false when it was already there.</summary>
    private bool Remember(HashSet<uint256> set, Queue<uint256> order, uint256 txId)
    {
        if (!set.Add(txId))
            return false;

        order.Enqueue(txId);
        while (order.Count > MaxRememberedMempoolTransactions)
            set.Remove(order.Dequeue());
        return true;
    }

    /// <summary>
    /// The mempool loop (O8): reads every <c>rawtx</c> message (topic, transaction, sequence) as it arrives. A message
    /// that is not a transaction is logged and skipped; the loop ends when the monitor stops.
    /// </summary>
    private void MonitorMempool(CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Starting mempool monitoring loop");

        var frames = new List<byte[]>(3);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var socket = _txSocket;
                if (socket is null)
                    return;

                if (!socket.TryReceiveMultipartBytes(TimeSpan.FromMilliseconds(100), ref frames, 3))
                    continue;

                if (frames.Count < 2 || Encoding.ASCII.GetString(frames[0]) != "rawtx")
                    continue;

                ProcessMempoolTransaction(Transaction.Load(frames[1], _network));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Error in the mempool monitoring loop");
            }
        }
    }

    /// <summary>
    /// Processes the queued blocks in order, rewinding first when a block does not extend the chain we processed.
    /// </summary>
    /// <remarks>
    /// Failure policy (NL-097): a block whose processing throws is retried up to
    /// <see cref="MaxBlockProcessingAttempts"/> times with exponential backoff. If it still fails, this round halts:
    /// the failing block and every later block stay queued, nothing is dropped, and the height is not advanced. The
    /// next round (the next ZMQ block, or a restart, which refetches the blocks from bitcoind) starts over from the
    /// failing block. Blocks are never skipped, because a skipped block could hide a funding confirmation, a deposit
    /// or a spend of a watched output. The pending broadcasts are sent again after every round, also after a halted
    /// one: a halt is when a failed channel's commitment most needs to reach the chain.
    /// </remarks>
    private async Task ProcessPendingBlocksAsync()
    {
        var cancellationToken = _cts?.Token ?? CancellationToken.None;

        await _blockBacklogSemaphore.WaitAsync(cancellationToken);
        try
        {
            await ProcessQueueAsync(cancellationToken);
        }
        finally
        {
            _blockBacklogSemaphore.Release();
        }

        await RebroadcastPendingAsync();
    }

    /// <summary>
    /// Processes the queue until it is empty, or until the round halts (then <see cref="IsChainProcessingHalted"/> is
    /// set).
    /// </summary>
    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
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
            var blockHash = new Hash(block.GetHash().ToBytes());

            if (DoesNotExtendProcessedChain(height, block))
            {
                if (await IsProcessedTipActiveAsync())
                {
                    // Our last processed block is still bitcoind's block at its height, so this block is not in the
                    // active chain (a late notification of a block that was reorged out): nothing to rewind
                    _logger.LogWarning(
                        "Block {Height} ({Hash}) is not in the active chain, which still holds every block we processed; dropping it",
                        height, block.GetHash());
                    _blocksToProcess.Remove(height);
                    if (height > _lastProcessedBlockHeight
                     && height <= await _bitcoinChainService.GetCurrentBlockHeightAsync())
                    {
                        // The stale block may have replaced the active block at its height in the queue
                        var active = await _bitcoinChainService.GetBlockAsync(height);
                        if (active is not null && active.GetHash() != block.GetHash())
                            _blocksToProcess[height] = active;
                    }

                    continue;
                }

                _logger.LogWarning("Block {Height} ({Hash}) does not extend the processed chain: reorg", height,
                                   block.GetHash());

                // The fork is searched from our own tip down (not from the incoming block's height), never above
                // bitcoind's tip
                var tip = await _bitcoinChainService.GetCurrentBlockHeightAsync();
                if (!await TryRewindAsync(Math.Min(_lastProcessedBlockHeight, tip)))
                {
                    Halt($"a reorg at height {height} has no fork point in the header ring of {HeaderRingSize} "
                       + "blocks");
                    return;
                }

                continue;
            }

            if (height < _lastProcessedBlockHeight)
            {
                // Already processed (a repeated notification, e.g. after reconsiderblock), or older than the ring
                if (!TryGetKnownHash(height, out var known) || !known.Equals(blockHash))
                    _logger.LogWarning("Skipping block {Height} below the last processed block {Last}", height,
                                       _lastProcessedBlockHeight);

                _blocksToProcess.Remove(height);
                continue;
            }

            if (!await TryProcessBlockWithRetriesAsync(block, height, cancellationToken))
            {
                Halt($"block {height} failed {MaxBlockProcessingAttempts} times in a row");
                _logger.LogCritical(
                    "Chain processing halted at block {Height} after {Attempts} failed attempts; {Pending} blocks remain queued and will be retried when the next block arrives or on restart",
                    height, MaxBlockProcessingAttempts, _blocksToProcess.Count);
                return;
            }
        }

        IsChainProcessingHalted = false;
        ChainProcessingHaltReason = null;
    }

    /// <summary>Sets the halt (NL-216): the flag, with a reason for the operator (<c>chainstatus</c>).</summary>
    private void Halt(string reason)
    {
        ChainProcessingHaltReason = reason;
        IsChainProcessingHalted = true;
    }

    /// <summary>
    /// True when bitcoind's block at our last processed height is the one we processed there, i.e. every block we
    /// processed is still in the active chain.
    /// </summary>
    private async Task<bool> IsProcessedTipActiveAsync()
    {
        if (!TryGetKnownHash(_lastProcessedBlockHeight, out var known))
            return false;

        if (await _bitcoinChainService.GetCurrentBlockHeightAsync() < _lastProcessedBlockHeight)
            return false;

        var chainHash = await _bitcoinChainService.GetBlockHashAsync(_lastProcessedBlockHeight);
        return known.Equals(new Hash(chainHash.ToBytes()));
    }

    /// <summary>
    /// True when the block cannot follow what we processed: its hash differs from the one we processed at its height,
    /// or it is the next block and its parent is not our tip. Without a stored hash to compare, false.
    /// </summary>
    private bool DoesNotExtendProcessedChain(uint height, Block block)
    {
        var blockHash = new Hash(block.GetHash().ToBytes());
        if (TryGetKnownHash(height, out var known))
            return !known.Equals(blockHash);

        if (height == _lastProcessedBlockHeight + 1 && TryGetKnownHash(_lastProcessedBlockHeight, out var tip))
            return !tip.Equals(new Hash(block.Header.HashPrevBlock.ToBytes()));

        return false;
    }

    /// <summary>The hash we processed at a height: from the header ring, or the state's hash for its own height.</summary>
    private bool TryGetKnownHash(uint height, out Hash hash)
    {
        if (_headers.TryGetValue(height, out var header))
        {
            hash = header.BlockHash;
            return true;
        }

        if (height == _blockchainState.LastProcessedHeight && !_blockchainState.LastProcessedBlockHash.Equals(Hash.Empty))
        {
            hash = _blockchainState.LastProcessedBlockHash;
            return true;
        }

        hash = Hash.Empty;
        return false;
    }

    private async Task<bool> TryProcessBlockWithRetriesAsync(Block block, uint height,
                                                             CancellationToken cancellationToken)
    {
        var delay = BlockRetryBaseDelay;
        for (var attempt = 1; attempt <= MaxBlockProcessingAttempts; attempt++)
        {
            BlockEffects? effects = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                effects = await StageBlockAsync(block, height, uow);
                await uow.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                effects = null;
                _logger.LogError(ex, "Error processing block at height {Height} (attempt {Attempt} of {MaxAttempts})",
                                 height, attempt, MaxBlockProcessingAttempts);
            }

            if (effects is not null)
            {
                ApplyBlock(effects);
                RaiseBlockEvents(effects);
                return true;
            }

            if (attempt < MaxBlockProcessingAttempts && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }

        return false;
    }

    /// <summary>
    /// Stages everything one block changes in <paramref name="uow"/> without touching memory, and returns what to
    /// apply and raise once the unit of work is saved.
    /// </summary>
    private async Task<BlockEffects> StageBlockAsync(Block block, uint height, IUnitOfWork uow)
    {
        var blockHash = new Hash(block.GetHash().ToBytes());
        var effects = new BlockEffects(height, blockHash,
                                       new BlockHeaderModel(height, blockHash,
                                                            new Hash(block.Header.HashPrevBlock.ToBytes())));
        var transactions = block.Transactions;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Processing block {Height} with {TxCount} transactions", height, transactions.Count);

        // Transactions we watch or broadcast. The index is the position within the block (all txs, coinbase
        // included), as BOLT 7 requires for short_channel_id.
        for (var index = 0; index < transactions.Count; index++)
        {
            var txId = transactions[index].GetHash();

            if (_watchedTransactions.TryGetValue(txId, out var watched) && watched.FirstSeenAtHeight is null
                                                                         && !effects.Watches.ContainsKey(txId))
            {
                _logger.LogInformation("Transaction {TxId} found in block at height {Height}", txId, height);

                var seen = new WatchedTransactionModel(watched.ChannelId, watched.TransactionId,
                                                       watched.RequiredDepth);
                seen.SetHeightAndIndex(height, (uint)index);
                uow.WatchedTransactionDbRepository.Update(seen);
                effects.Watches[txId] = seen;

                // A channel's funding transaction: from now on its funding output is watched (both roles)
                var fundingWatch =
                    await uow.WatchedOutpointDbRepository.AddFundingOutpointIfMissingAsync(seen.ChannelId,
                        seen.TransactionId);
                if (fundingWatch is not null)
                    effects.NewOutpoints.Add(fundingWatch);
            }

            if (_pendingBroadcasts.ContainsKey(txId))
            {
                await uow.BroadcastTransactionDbRepository.MarkConfirmedAsync(new TxId(txId.ToBytes()), height,
                                                                               blockHash);
                effects.ConfirmedBroadcasts.Add(txId);
            }
        }

        StageWalletMovements(transactions, height, uow, effects);
        await StageWatchedSpendsAsync(transactions, height, blockHash, uow, effects);
        StageWatchedTransactionDepths(height, uow, effects);

        // Blockchain state (a replay of the last processed block keeps its height)
        effects.State = _blockchainState with { };
        if (height >= effects.State.LastProcessedHeight)
            effects.State.UpdateState(blockHash, height);
        uow.BlockchainStateDbRepository.Update(effects.State);

        // Header ring
        await uow.BlockHeaderDbRepository.AddOrReplaceAsync(effects.Header);
        if (height >= HeaderRingSize)
            await uow.BlockHeaderDbRepository.DeleteBelowAsync(height - (uint)HeaderRingSize + 1);

        return effects;
    }

    /// <summary>Stages the spends of every watched outpoint the block's transactions spend.</summary>
    private async Task StageWatchedSpendsAsync(List<Transaction> transactions, uint height, Hash blockHash,
                                               IUnitOfWork uow, BlockEffects effects)
    {
        if (_watchedOutpoints.IsEmpty && effects.NewOutpoints.Count == 0)
            return;

        for (var index = 0; index < transactions.Count; index++)
        {
            var transaction = transactions[index];
            foreach (var input in transaction.Inputs)
            {
                if (!_watchedOutpoints.TryGetValue(input.PrevOut, out var channelId))
                {
                    var added = effects.NewOutpoints.FirstOrDefault(o => o.OutputIndex == input.PrevOut.N
                                                                      && new uint256(o.TransactionId)
                                                                     == input.PrevOut.Hash);
                    if (added is null)
                        continue;

                    channelId = added.ChannelId;
                }

                var txId = transaction.GetHash();
                _logger.LogInformation(
                    "Watched outpoint {Outpoint} of channel {ChannelId} spent by {TxId} at height {Height}",
                    input.PrevOut, channelId, txId, height);

                var spentTxId = new TxId(input.PrevOut.Hash.ToBytes());
                var spendingTxId = new TxId(txId.ToBytes());
                await uow.WatchedOutpointDbRepository.MarkSpentAsync(spentTxId, input.PrevOut.N, spendingTxId,
                                                                     height, blockHash);
                effects.Spends.Add(new OutpointSpentEventArgs(channelId,
                                                              new SignedTransaction(spendingTxId,
                                                                                    transaction.ToBytes()),
                                                              height, (uint)index, spentTxId, input.PrevOut.N,
                                                              blockHash));
            }
        }
    }

    private void StageWalletMovements(List<Transaction> transactions, uint blockHeight, IUnitOfWork uow,
                                      BlockEffects effects)
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

                // Save Utxo to the database (memory follows the save, UnitOfWork)
                var utxo = new UtxoModel(txId.ToBytes(), (uint)i, LightningMoney.Satoshis(output.Value.Satoshi),
                                         blockHeight, watchedAddress);
                uow.AddUtxo(utxo);

                // The address stays watched (as after a restart, which reloads every wallet address): the wallet hands
                // an address out again once its deposits are spent, and two channels closing at once can get the
                // same shutdown address, so a later deposit to it must be found too
                effects.Movements.Add(new WalletMovementEventArgs(destinationAddress.ToString(),
                                                                  LightningMoney.Satoshis(output.Value.Satoshi),
                                                                  txId.ToBytes(), blockHeight));
            }

            // Check each input for spent utxos
            foreach (var input in transaction.Inputs)
                uow.TrySpendUtxo(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N);
        }
    }

    /// <summary>Stages the completion of every watched transaction that reaches its depth at this height.</summary>
    private void StageWatchedTransactionDepths(uint height, IUnitOfWork uow, BlockEffects effects)
    {
        foreach (var (txId, memoryWatch) in _watchedTransactions)
        {
            var watch = effects.Watches.GetValueOrDefault(txId) ?? memoryWatch;
            if (watch.IsCompleted || watch.FirstSeenAtHeight is not { } firstSeen || firstSeen > height)
                continue;

            // The FirstSeenAtHeight represents 1 confirmation, so we have to add 1
            var confirmations = height - firstSeen + 1;
            if (confirmations < watch.RequiredDepth)
                continue;

            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation(
                    "Transaction {TxId} reached required depth of {depth} confirmations at block {blockHeight}",
                    watch.TransactionId, watch.RequiredDepth, height);

            var completed = CopyWatch(watch);
            completed.MarkAsCompleted();
            uow.WatchedTransactionDbRepository.Update(completed);
            effects.Watches[txId] = completed;
            effects.Confirmed.Add(completed);
        }
    }

    /// <summary>Applies a saved block to memory.</summary>
    private void ApplyBlock(BlockEffects effects)
    {
        foreach (var (txId, watch) in effects.Watches)
        {
            if (watch.IsCompleted)
                _watchedTransactions.TryRemove(txId, out _);
            else
                _watchedTransactions[txId] = watch;
        }

        foreach (var outpoint in effects.NewOutpoints)
            TrackWatchedOutpoint(outpoint);

        foreach (var txId in effects.ConfirmedBroadcasts)
        {
            _pendingBroadcasts.TryRemove(txId, out _);
            _refusals.TryRemove(txId, out _);
        }

        _headers[effects.Height] = effects.Header;
        while (_headers.Count > HeaderRingSize)
            _headers.Remove(_headers.Keys.First());

        _blockchainState = effects.State!;
        _blocksToProcess.Remove(effects.Height);
        if (effects.Height > _lastProcessedBlockHeight || _lastProcessedBlockHeight == effects.State!.LastProcessedHeight)
            _lastProcessedBlockHeight = effects.State!.LastProcessedHeight;
    }

    /// <summary>Raises a saved block's events: the block, confirmations, wallet movements, outpoint spends.</summary>
    private void RaiseBlockEvents(BlockEffects effects)
    {
        Raise(() => OnNewBlockDetected?.Invoke(this, new NewBlockEventArgs(effects.Height, effects.BlockHash)),
              "new block");

        foreach (var confirmed in effects.Confirmed)
            Raise(() => OnTransactionConfirmed?.Invoke(this, new TransactionConfirmedEventArgs(confirmed,
                                                          effects.Height)), "transaction confirmation");

        foreach (var movement in effects.Movements)
            Raise(() => OnWalletMovementDetected?.Invoke(this, movement), "wallet movement");

        foreach (var spend in effects.Spends)
            Raise(() => OnWatchedOutpointSpent?.Invoke(this, spend), "outpoint spend");
    }

    private void Raise(Action raise, string what)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A {What} handler failed", what);
        }
    }

    /// <summary>
    /// Rewinds to the highest block at or below <paramref name="searchFrom"/> that is still in the active chain: rolls
    /// back what the disconnected blocks changed in one save, reloads memory, raises
    /// <see cref="OnBlockDisconnected"/> and queues the active chain from the fork point up to its tip. False when no
    /// fork point is found within the header ring or the rollback fails.
    /// </summary>
    private async Task<bool> TryRewindAsync(uint searchFrom)
    {
        try
        {
            uint? fork = null;
            for (var height = (long)searchFrom; height >= 0; height--)
            {
                if (!TryGetKnownHash((uint)height, out var known))
                    break;

                var chainHash = await _bitcoinChainService.GetBlockHashAsync((uint)height);
                if (known.Equals(new Hash(chainHash.ToBytes())))
                {
                    fork = (uint)height;
                    break;
                }
            }

            if (fork is not { } forkHeight || !TryGetKnownHash(forkHeight, out var forkHash))
            {
                _logger.LogCritical(
                    "Reorg below block {Height} is deeper than the {Ring} blocks we keep; chain processing halted",
                    searchFrom, HeaderRingSize);
                return false;
            }

            var disconnected = _headers.Values.Where(h => h.Height > forkHeight).OrderByDescending(h => h.Height)
                                       .ToList();
            if (_lastProcessedBlockHeight > forkHeight && disconnected.All(h => h.Height != _lastProcessedBlockHeight))
                disconnected.Insert(0, new BlockHeaderModel(_lastProcessedBlockHeight,
                                                            _blockchainState.LastProcessedBlockHash, Hash.Empty));

            var rewoundState = new BlockchainState(forkHeight, forkHash, DateTime.UtcNow) { Id = _blockchainState.Id };

            // NL-293: wallet outputs spent in the disconnected blocks that are unspent in the active chain again (a
            // failed lookup restores nothing: the rewind itself must not fail over it)
            List<(OutPoint OutPoint, TxOut Output, uint Height, WalletAddressModel Address)> restoredUtxos;
            try
            {
                restoredUtxos = await FindWalletOutputsUnspentAgainAsync(disconnected);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not look up the wallet outputs spent in the disconnected blocks; they are "
                                   + "not restored");
                restoredUtxos = [];
            }

            IReadOnlyList<WatchedTransactionModel> completedInDisconnected;
            using (var scope = _serviceProvider.CreateScope())
            {
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                completedInDisconnected =
                    await uow.WatchedTransactionDbRepository.GetCompletedFirstSeenAboveAsync(forkHeight);
                var resetWatches = await uow.WatchedTransactionDbRepository.ResetPendingFirstSeenAboveAsync(forkHeight);

                // NL-292: a watch that completed in a disconnected block is pending again, so its confirmation (the
                // funding depth and the short channel id) is raised again from its position on the new branch
                foreach (var completed in completedInDisconnected)
                    uow.WatchedTransactionDbRepository.Update(new WatchedTransactionModel(completed.ChannelId,
                        completed.TransactionId, completed.RequiredDepth));

                var clearedSpends = await uow.WatchedOutpointDbRepository.ClearSpendsAboveAsync(forkHeight);
                var unconfirmed = await uow.BroadcastTransactionDbRepository.UnconfirmAboveAsync(forkHeight);
                var (removedDeposits, restoredSpends) = await StageWalletRollbackAsync(uow, forkHeight, restoredUtxos);
                await uow.BlockHeaderDbRepository.DeleteAboveAsync(forkHeight);
                uow.BlockchainStateDbRepository.Update(rewoundState);
                await uow.SaveChangesAsync();

                _logger.LogWarning(
                    "Reorg: rewound from block {From} to fork point {Fork} ({Count} blocks disconnected); reset {Watches} watched transactions ({Completed} of them completed), {Spends} outpoint spends and {Broadcasts} broadcast confirmations; removed {Deposits} wallet deposits and restored {Restored} wallet outputs",
                    _lastProcessedBlockHeight, forkHeight, disconnected.Count, resetWatches + completedInDisconnected.Count,
                    completedInDisconnected.Count, clearedSpends, unconfirmed, removedDeposits, restoredSpends);
            }

            foreach (var watch in completedInDisconnected)
                _logger.LogWarning(
                    "Transaction {TxId} of channel {ChannelId} had reached its depth in block {Height}, which was disconnected; it is watched again from the new branch",
                    watch.TransactionId, watch.ChannelId, watch.FirstSeenAtHeight);

            foreach (var header in disconnected)
                _headers.Remove(header.Height);
            _blockchainState = rewoundState;
            _lastProcessedBlockHeight = forkHeight;

            // Memory follows the saved rows
            using (var scope = _serviceProvider.CreateScope())
            {
                using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                _watchedTransactions.Clear();
                await LoadPendingWatchedTransactionsAsync(uow);
                foreach (var broadcast in await uow.BroadcastTransactionDbRepository.GetPendingAsync())
                    _pendingBroadcasts[new uint256(broadcast.TransactionId)] = broadcast;
            }

            _blocksToProcess.Clear();
            var tip = await _bitcoinChainService.GetCurrentBlockHeightAsync();
            // The new branch may be shorter than the old one: never fetch above its tip
            _catchUpHeight = tip + 1;
            await FillQueueFromChainAsync();

            foreach (var header in disconnected)
                Raise(() => OnBlockDisconnected?.Invoke(this, new BlockDisconnectedEventArgs(header.Height,
                                                            header.BlockHash, forkHeight)), "block disconnected");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Rewinding the chain below block {Height} failed; chain processing halted",
                                searchFrom);
            return false;
        }
    }

    /// <summary>
    /// The wallet outputs that inputs of the disconnected blocks spent and that are unspent in the active chain again
    /// (NL-293). A spent wallet row is deleted, so the output is read back from bitcoind (<c>gettxout</c> with the
    /// mempool). An output whose spend is back in the mempool (the usual case) is not restored: coin selection would
    /// otherwise double-spend our own pending transaction (a funding transaction replaced by full-RBF); it is left to
    /// that spend confirming again. Candidates: P2WPKH inputs whose key is a wallet address, and every taproot key-path
    /// input (its witness names no key); bitcoind confirms the script.
    /// </summary>
    private async Task<List<(OutPoint OutPoint, TxOut Output, uint Height, WalletAddressModel Address)>>
        FindWalletOutputsUnspentAgainAsync(IReadOnlyList<BlockHeaderModel> disconnected)
    {
        var found = new List<(OutPoint, TxOut, uint, WalletAddressModel)>();
        if (_watchedAddresses.IsEmpty)
            return found;

        foreach (var header in disconnected)
        {
            if (header.BlockHash.Equals(Hash.Empty))
                continue;

            var block = await _bitcoinChainService.GetBlockAsync(new uint256((byte[])header.BlockHash));
            if (block is null)
            {
                _logger.LogWarning("Disconnected block {Height} ({Hash}) can't be read; wallet outputs it spent are "
                                 + "not restored", header.Height, header.BlockHash);
                continue;
            }

            foreach (var transaction in block.Transactions.Where(t => !t.IsCoinBase))
            {
                foreach (var input in transaction.Inputs)
                {
                    if (!MayBeWalletInput(input))
                        continue;

                    var unspent = await _bitcoinChainService.GetUnspentOutputAsync(input.PrevOut);
                    if (unspent is not { } output
                     || output.Output.ScriptPubKey.GetDestinationAddress(_network) is not { } address
                     || !_watchedAddresses.TryGetValue(address.ToString(), out var walletAddress))
                        continue;

                    found.Add((input.PrevOut, output.Output, output.Height, walletAddress));
                }
            }
        }

        return found;
    }

    /// <summary>True for an input that may spend one of our wallet outputs (see
    /// <see cref="FindWalletOutputsUnspentAgainAsync"/>).</summary>
    private bool MayBeWalletInput(TxIn input)
    {
        var witness = input.WitScript.Pushes.ToArray();
        if (witness is [_, { Length: 33 } pubKey])
        {
            try
            {
                var address = new PubKey(pubKey).WitHash.GetAddress(_network).ToString();
                return _watchedAddresses.ContainsKey(address);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // Taproot key path: one 64- or 65-byte signature
        return witness is [{ Length: 64 or 65 }];
    }

    /// <summary>
    /// Stages the wallet rollback of a reorg (NL-293): deposits confirmed above the fork are removed (they come back when
    /// their transaction is mined on the new branch), outputs a disconnected block spent are added back.
    /// </summary>
    private async Task<(int Removed, int Restored)> StageWalletRollbackAsync(
        IUnitOfWork uow, uint forkHeight,
        IReadOnlyList<(OutPoint OutPoint, TxOut Output, uint Height, WalletAddressModel Address)> restored)
    {
        var removed = 0;
        var unspent = await uow.UtxoDbRepository.GetUnspentAsync() ?? [];
        foreach (var deposit in unspent.Where(u => u.BlockHeight > forkHeight))
        {
            if (deposit.LockedToChannelId is { } channelId && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Wallet output {TxId}:{Index} locked to channel {ChannelId} was confirmed in a "
                                 + "disconnected block; it is removed until its transaction confirms again",
                                   deposit.TxId, deposit.Index, channelId);
            uow.TrySpendUtxo(deposit.TxId, deposit.Index);
            removed++;
        }

        var utxoMemoryRepository = _serviceProvider.GetService<IUtxoMemoryRepository>();
        var count = 0;
        foreach (var (outPoint, output, height, address) in restored)
        {
            var txId = new TxId(outPoint.Hash.ToBytes());
            if (utxoMemoryRepository?.TryGetUtxo(txId, outPoint.N, out _) == true)
                continue;

            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Wallet output {OutPoint} ({Amount}) was spent in a disconnected block; it is "
                                 + "spendable again", outPoint, output.Value);
            uow.AddUtxo(new UtxoModel(txId, outPoint.N, LightningMoney.Satoshis(output.Value.Satoshi), height,
                                      address));
            count++;
        }

        return (removed, count);
    }

    /// <summary>
    /// Sends every pending broadcast again (after every processing round and at startup), after dropping those whose
    /// stored row is no longer pending (replaced by an RBF bump, abandoned, NL-294).
    /// </summary>
    private async Task RebroadcastPendingAsync()
    {
        await DropSettledBroadcastsAsync();
        foreach (var broadcast in _pendingBroadcasts.Values.ToList())
            await TrySendAsync(broadcast);
    }

    /// <summary>
    /// Forgets the in-memory pending broadcasts whose stored row another component moved out of
    /// <see cref="BroadcastState.Pending"/> (the sweep scheduler's <see cref="BroadcastState.Replaced"/>, the watcher's
    /// or scheduler's <see cref="BroadcastState.Abandoned"/>): they are never sent again. A transaction without a row is
    /// kept. A failed read keeps them all (they are sent once more).
    /// </summary>
    private async Task DropSettledBroadcastsAsync()
    {
        if (_pendingBroadcasts.IsEmpty)
            return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            foreach (var txId in _pendingBroadcasts.Keys.ToList())
            {
                var stored = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(
                                 new TxId(txId.ToBytes()));
                if (stored is null || stored.State == BroadcastState.Pending)
                    continue;

                _pendingBroadcasts.TryRemove(txId, out _);
                _refusals.TryRemove(txId, out _);
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("{Purpose} transaction {TxId} is {State}; it is no longer rebroadcast",
                                           Enum.GetName(stored.Purpose), txId, Enum.GetName(stored.State));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not check the stored state of the pending broadcasts");
        }
    }

    /// <summary>
    /// Sends a stored broadcast. True when the node accepted it or already has it; false when it was refused. A refusal
    /// is logged at Warning the first time and then every <see cref="RefusalWarningInterval"/> refusals in a row (at
    /// Debug in between), so a transaction the node keeps refusing stays visible without a line per block.
    /// </summary>
    private async Task<bool> TrySendAsync(BroadcastTransactionModel broadcast)
    {
        var txId = new uint256(broadcast.TransactionId);
        try
        {
            await _bitcoinChainService.SendTransactionAsync(Transaction.Load(broadcast.RawTransaction, _network));
            _refusals.TryRemove(txId, out _);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsAlreadyKnown(ex))
            {
                _refusals.TryRemove(txId, out _);
                return true;
            }

            var refusals = _refusals.AddOrUpdate(txId, 1, (_, count) => count + 1);
            var level = refusals == 1 || refusals % RefusalWarningInterval == 0 ? LogLevel.Warning : LogLevel.Debug;
            if (_logger.IsEnabled(level))
                _logger.Log(level, ex,
                            "Broadcast of {Purpose} transaction {TxId} was refused ({Refusals} time(s) in a row); it is sent again after the next block",
                            Enum.GetName(broadcast.Purpose), txId, refusals);
            return false;
        }
    }

    private static bool IsAlreadyKnown(Exception sendError)
    {
        if (sendError is RPCException { RPCCode: RPCErrorCode.RPC_VERIFY_ALREADY_IN_CHAIN })
            return true;

        var message = sendError.Message;
        return s_alreadyKnownRejections.Any(r => message.Contains(r, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="upToExclusive">Queue the missing blocks below this height.</param>
    private async Task AddMissingBlocksToProcessAsync(uint upToExclusive)
    {
        if (upToExclusive > _catchUpHeight)
            _catchUpHeight = upToExclusive;

        if (upToExclusive > _lastProcessedBlockHeight + 1)
            _logger.LogWarning("Processing missed blocks from height {LastProcessedHeight} to {CurrentHeight}",
                               _lastProcessedBlockHeight + 1, upToExclusive - 1);

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

    private static WatchedTransactionModel CopyWatch(WatchedTransactionModel watch)
    {
        var copy = new WatchedTransactionModel(watch.ChannelId, watch.TransactionId, watch.RequiredDepth);
        if (watch is { FirstSeenAtHeight: { } height, TransactionIndex: { } index })
            copy.SetHeightAndIndex(height, index);
        if (watch.IsCompleted)
            copy.MarkAsCompleted();

        return copy;
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

        // The signer derives each input's key from the UTXO's wallet address, so load it too (NL-302)
        var utxoSet = (await uow.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true)).ToList();
        if (utxoSet.Count > 0)
        {
            var utxoMemoryRepository = _serviceProvider.GetService<IUtxoMemoryRepository>() ??
                                       throw new InvalidOperationException(
                                           $"Error getting required service {nameof(IUtxoMemoryRepository)}");
            utxoMemoryRepository.Load(utxoSet);
        }
    }

    /// <summary>What one block changes, staged before the save and applied (and raised) after it.</summary>
    private sealed class BlockEffects(uint height, Hash blockHash, BlockHeaderModel header)
    {
        public uint Height { get; } = height;
        public Hash BlockHash { get; } = blockHash;
        public BlockHeaderModel Header { get; } = header;
        public BlockchainState? State { get; set; }
        public Dictionary<uint256, WatchedTransactionModel> Watches { get; } = [];
        public List<WatchedTransactionModel> Confirmed { get; } = [];
        public List<WatchedOutpointModel> NewOutpoints { get; } = [];
        public List<uint256> ConfirmedBroadcasts { get; } = [];
        public List<WalletMovementEventArgs> Movements { get; } = [];
        public List<OutpointSpentEventArgs> Spends { get; } = [];
    }
}