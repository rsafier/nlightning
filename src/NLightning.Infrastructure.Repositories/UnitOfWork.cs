using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Repositories;

using Database.Bitcoin;
using Database.Channel;
using Database.Node;
using Database.Onchain;
using Database.Payment;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Hashes;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Onchain.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Persistence.Contexts;

public class UnitOfWork : IUnitOfWork
{
    private readonly NLightningDbContext _context;
    private readonly ILogger<UnitOfWork> _logger;
    private readonly ISha256 _sha256;
    private readonly TimeProvider _timeProvider;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    private readonly List<(PendingUtxoChange Change, UtxoModel Utxo)> _pendingUtxoChanges = [];

    // Bitcoin repositories
    private BlockchainStateDbRepository? _blockchainStateDbRepository;
    private WatchedTransactionDbRepository? _watchedTransactionDbRepository;
    private WalletAddressesDbRepository? _walletAddressesDbRepository;
    private UtxoDbRepository? _utxoDbRepository;

    // On-chain repositories
    private WatchedOutpointDbRepository? _watchedOutpointDbRepository;
    private BroadcastTransactionDbRepository? _broadcastTransactionDbRepository;
    private BlockHeaderDbRepository? _blockHeaderDbRepository;
    private RevokedCommitmentDbRepository? _revokedCommitmentDbRepository;
    private OnchainResolutionDbRepository? _onchainResolutionDbRepository;

    // Channel repositories
    private ChannelConfigDbRepository? _channelConfigDbRepository;
    private ChannelDbRepository? _channelDbRepository;
    private ChannelKeySetDbRepository? _channelKeySetDbRepository;
    private ChannelStateDbRepository? _channelStateDbRepository;
    private RemoteShachainDbRepository? _remoteShachainDbRepository;

    // Node repositories
    private PeerDbRepository? _peerDbRepository;

    // Payment repositories
    private InvoiceDbRepository? _invoiceDbRepository;
    private PaymentDbRepository? _paymentDbRepository;
    private ForwardCircuitDbRepository? _forwardCircuitDbRepository;

    // Onion replay set
    private OnionReplayDbRepository? _onionReplayDbRepository;

    public IBlockchainStateDbRepository BlockchainStateDbRepository =>
        _blockchainStateDbRepository ??= new BlockchainStateDbRepository(_context);

    public IWatchedTransactionDbRepository WatchedTransactionDbRepository =>
        _watchedTransactionDbRepository ??= new WatchedTransactionDbRepository(_context);

    public IWalletAddressesDbRepository WalletAddressesDbRepository =>
        _walletAddressesDbRepository ??= new WalletAddressesDbRepository(_context);

    public IUtxoDbRepository UtxoDbRepository => _utxoDbRepository ??= new UtxoDbRepository(_context);

    public IWatchedOutpointDbRepository WatchedOutpointDbRepository =>
        _watchedOutpointDbRepository ??= new WatchedOutpointDbRepository(_context);

    public IBroadcastTransactionDbRepository BroadcastTransactionDbRepository =>
        _broadcastTransactionDbRepository ??= new BroadcastTransactionDbRepository(_context);

    public IBlockHeaderDbRepository BlockHeaderDbRepository =>
        _blockHeaderDbRepository ??= new BlockHeaderDbRepository(_context);

    public IRevokedCommitmentDbRepository RevokedCommitmentDbRepository =>
        _revokedCommitmentDbRepository ??= new RevokedCommitmentDbRepository(_context);

    public IOnchainResolutionDbRepository OnchainResolutionDbRepository =>
        _onchainResolutionDbRepository ??= new OnchainResolutionDbRepository(_context);

    public IChannelConfigDbRepository ChannelConfigDbRepository =>
        _channelConfigDbRepository ??= new ChannelConfigDbRepository(_context);

    public IChannelDbRepository ChannelDbRepository =>
        _channelDbRepository ??= new ChannelDbRepository(_context, _sha256, _logger);

    public IChannelKeySetDbRepository ChannelKeySetDbRepository =>
        _channelKeySetDbRepository ??= new ChannelKeySetDbRepository(_context);

    public IChannelStateDbRepository ChannelStateDbRepository =>
        _channelStateDbRepository ??= new ChannelStateDbRepository(_context, _timeProvider);

    public IRemoteShachainDbRepository RemoteShachainDbRepository =>
        _remoteShachainDbRepository ??= new RemoteShachainDbRepository(_context);

    public IPeerDbRepository PeerDbRepository =>
        _peerDbRepository ??= new PeerDbRepository(_context);

    public IInvoiceDbRepository InvoiceDbRepository => _invoiceDbRepository ??= new InvoiceDbRepository(_context);

    public IPaymentDbRepository PaymentDbRepository => _paymentDbRepository ??= new PaymentDbRepository(_context);

    public IForwardCircuitDbRepository ForwardCircuitDbRepository =>
        _forwardCircuitDbRepository ??= new ForwardCircuitDbRepository(_context);

    public IOnionReplayDbRepository OnionReplayDbRepository =>
        _onionReplayDbRepository ??= new OnionReplayDbRepository(_context);

    /// <param name="context">The scope's database context.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sha256">The hasher the channel repository uses.</param>
    /// <param name="utxoMemoryRepository">The in-memory UTXO set, updated after a successful save.</param>
    /// <param name="timeProvider">The clock that stamps new HTLC rows (<c>HtlcEntity.AddedAt</c>, the start of the
    /// BOLT 4 hold time); <see cref="TimeProvider.System"/> when null.</param>
    public UnitOfWork(NLightningDbContext context, ILogger<UnitOfWork> logger, ISha256 sha256,
                      IUtxoMemoryRepository utxoMemoryRepository, TimeProvider? timeProvider = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;
        _sha256 = sha256;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _utxoMemoryRepository = utxoMemoryRepository;
    }

    public async Task<ICollection<PeerModel>> GetPeersForStartupAsync()
    {
        var peers = await PeerDbRepository.GetAllAsync();
        var peerList = peers.ToList();
        foreach (var peer in peerList)
        {
            var channels = await ChannelDbRepository.GetByPeerIdAsync(peer.NodeId);
            var channelList = channels.ToList();
            if (channelList.Count > 0)
                peer.Channels = channelList as List<ChannelModel>;
        }

        return peerList;
    }

    public void AddUtxo(UtxoModel utxoModel)
    {
        if (_utxoMemoryRepository.TryGetUtxo(utxoModel.TxId, utxoModel.Index, out _)
         || TryGetPendingUtxoAdd(utxoModel.TxId, utxoModel.Index, out _))
            throw new InvalidOperationException("Cannot add Utxo");

        // Stage the database change first. The memory repository is only updated after a successful save, so a
        // failure here or at SaveChanges time never leaves memory and the database out of sync.
        try
        {
            UtxoDbRepository.Add(utxoModel);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to add Utxo to the database");
            throw;
        }

        _pendingUtxoChanges.Add((PendingUtxoChange.Add, utxoModel));
    }

    public void TrySpendUtxo(TxId transactionId, uint index)
    {
        // Check if utxo exists in memory or was added in this unit of work
        if (!_utxoMemoryRepository.TryGetUtxo(transactionId, index, out var utxoModel)
         && !TryGetPendingUtxoAdd(transactionId, index, out utxoModel))
            return;

        if (_pendingUtxoChanges.Contains((PendingUtxoChange.Spend, utxoModel)))
            return;

        try
        {
            UtxoDbRepository.Spend(utxoModel);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to spend Utxo from the database");
            throw;
        }

        _pendingUtxoChanges.Add((PendingUtxoChange.Spend, utxoModel));
    }

    public void SaveChanges()
    {
        _context.SaveChanges();
        ApplyPendingUtxoChanges();
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
        ApplyPendingUtxoChanges();
    }

    private bool TryGetPendingUtxoAdd(TxId transactionId, uint index, [MaybeNullWhen(false)] out UtxoModel utxoModel)
    {
        foreach (var (change, pendingUtxo) in _pendingUtxoChanges)
        {
            if (change != PendingUtxoChange.Add || !pendingUtxo.TxId.Equals(transactionId) ||
                pendingUtxo.Index != index)
                continue;

            utxoModel = pendingUtxo;
            return true;
        }

        utxoModel = null;
        return false;
    }

    private void ApplyPendingUtxoChanges()
    {
        foreach (var (change, utxoModel) in _pendingUtxoChanges)
        {
            try
            {
                if (change == PendingUtxoChange.Add)
                    _utxoMemoryRepository.Add(utxoModel);
                else
                    _utxoMemoryRepository.Spend(utxoModel);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to apply saved Utxo change to memory repository");
            }
        }

        _pendingUtxoChanges.Clear();
    }

    private enum PendingUtxoChange
    {
        Add,
        Spend
    }

    #region Dispose Pattern

    private bool _disposed;

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
                _context.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    #endregion
}