using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Repositories;

using Database.Accounting;
using Database.Bitcoin;
using Database.Channel;
using Database.Gossip;
using Database.Node;
using Database.Onchain;
using Database.Payment;
using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Hashes;
using Domain.Gossip.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.PeerStorage;
using Domain.Offers.Interfaces;
using Domain.Onchain.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Persistence.Contexts;

public class UnitOfWork : IUnitOfWork
{
    private readonly NLightningDbContext _context;
    private readonly ILogger<UnitOfWork> _logger;
    private readonly ISha256 _sha256;
    private readonly TimeProvider _timeProvider;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;
    private readonly ulong? _maxDustHtlcExposureMsat;
    private readonly List<(PendingUtxoChange Change, UtxoModel Utxo)> _pendingUtxoChanges = [];

    // Bitcoin repositories
    private BlockchainStateDbRepository? _blockchainStateDbRepository;
    private WatchedTransactionDbRepository? _watchedTransactionDbRepository;
    private WalletAddressesDbRepository? _walletAddressesDbRepository;
    private UtxoDbRepository? _utxoDbRepository;
    private FeeInputReservationDbRepository? _feeInputReservationDbRepository;

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
    private ChannelSigningInfoDbRepository? _channelSigningInfoDbRepository;
    private InteractiveTxSessionDbRepository? _interactiveTxSessionDbRepository;
    private ChannelFundingDbRepository? _channelFundingDbRepository;
    private ChannelPolicyDbRepository? _channelPolicyDbRepository;

    // Gossip graph
    private GraphDbRepository? _graphDbRepository;

    // Node repositories
    private PeerDbRepository? _peerDbRepository;
    private PeerStorageDbRepository? _peerStorageDbRepository;
    private PeerStorageRetrievalDbRepository? _peerStorageRetrievalDbRepository;

    // Payment repositories
    private InvoiceDbRepository? _invoiceDbRepository;
    private PaymentDbRepository? _paymentDbRepository;
    private PaymentPartDbRepository? _paymentPartDbRepository;
    private ForwardCircuitDbRepository? _forwardCircuitDbRepository;

    // Onion replay set
    private OnionReplayDbRepository? _onionReplayDbRepository;

    // BOLT 12 offers
    private OfferDbRepository? _offerDbRepository;

    // Accounting feed (NL-602)
    private AccountingEventDbRepository? _accountingEventDbRepository;
    private readonly AccountingFeedGate? _accountingFeedGate;
    private AccountingBooksDbRepository? _accountingBooksDbRepository;

    // Accounting financial books (NL-602 A3, migration AddAccountingFinancial)
    private AccountingPriceDbRepository? _accountingPriceDbRepository;
    private AccountingRuleDbRepository? _accountingRuleDbRepository;
    private AccountingOverrideDbRepository? _accountingOverrideDbRepository;
    private AccountingLotDbRepository? _accountingLotDbRepository;
    private AccountingPeriodDbRepository? _accountingPeriodDbRepository;

    public IBlockchainStateDbRepository BlockchainStateDbRepository =>
        _blockchainStateDbRepository ??= new BlockchainStateDbRepository(_context);

    public IWatchedTransactionDbRepository WatchedTransactionDbRepository =>
        _watchedTransactionDbRepository ??= new WatchedTransactionDbRepository(_context);

    public IWalletAddressesDbRepository WalletAddressesDbRepository =>
        _walletAddressesDbRepository ??= new WalletAddressesDbRepository(_context);

    public IUtxoDbRepository UtxoDbRepository => _utxoDbRepository ??= new UtxoDbRepository(_context);

    public IFeeInputReservationDbRepository FeeInputReservationDbRepository =>
        _feeInputReservationDbRepository ??= new FeeInputReservationDbRepository(_context);

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
        _channelDbRepository ??= new ChannelDbRepository(_context, _sha256, _logger, _maxDustHtlcExposureMsat);

    public IChannelKeySetDbRepository ChannelKeySetDbRepository =>
        _channelKeySetDbRepository ??= new ChannelKeySetDbRepository(_context);

    public IChannelStateDbRepository ChannelStateDbRepository =>
        _channelStateDbRepository ??= new ChannelStateDbRepository(_context, _timeProvider, _logger);

    public IRemoteShachainDbRepository RemoteShachainDbRepository =>
        _remoteShachainDbRepository ??= new RemoteShachainDbRepository(_context);

    public IChannelSigningInfoDbRepository ChannelSigningInfoDbRepository =>
        _channelSigningInfoDbRepository ??= new ChannelSigningInfoDbRepository(_context);

    public IGraphDbRepository GraphDbRepository => _graphDbRepository ??= new GraphDbRepository(_context);

    public IPeerDbRepository PeerDbRepository =>
        _peerDbRepository ??= new PeerDbRepository(_context);

    public IPeerStorageDbRepository PeerStorageDbRepository =>
        _peerStorageDbRepository ??= new PeerStorageDbRepository(_context);

    public IPeerStorageRetrievalDbRepository PeerStorageRetrievalDbRepository =>
        _peerStorageRetrievalDbRepository ??= new PeerStorageRetrievalDbRepository(_context);

    public IInvoiceDbRepository InvoiceDbRepository => _invoiceDbRepository ??= new InvoiceDbRepository(_context);

    public IPaymentDbRepository PaymentDbRepository => _paymentDbRepository ??= new PaymentDbRepository(_context);

    public IPaymentPartDbRepository PaymentPartDbRepository =>
        _paymentPartDbRepository ??= new PaymentPartDbRepository(_context);

    public IForwardCircuitDbRepository ForwardCircuitDbRepository =>
        _forwardCircuitDbRepository ??= new ForwardCircuitDbRepository(_context);

    public IOnionReplayDbRepository OnionReplayDbRepository =>
        _onionReplayDbRepository ??= new OnionReplayDbRepository(_context);

    public IOfferDbRepository OfferDbRepository => _offerDbRepository ??= new OfferDbRepository(_context);

    public IAccountingEventDbRepository AccountingEventDbRepository =>
        _accountingEventDbRepository ??= new AccountingEventDbRepository(_context, _accountingFeedGate);

    public IAccountingBooksDbRepository AccountingBooksDbRepository =>
        _accountingBooksDbRepository ??= new AccountingBooksDbRepository(_context);

    public IAccountingPriceDbRepository AccountingPriceDbRepository =>
        _accountingPriceDbRepository ??= new AccountingPriceDbRepository(_context);

    public IAccountingRuleDbRepository AccountingRuleDbRepository =>
        _accountingRuleDbRepository ??= new AccountingRuleDbRepository(_context);

    public IAccountingOverrideDbRepository AccountingOverrideDbRepository =>
        _accountingOverrideDbRepository ??= new AccountingOverrideDbRepository(_context);

    public IAccountingLotDbRepository AccountingLotDbRepository =>
        _accountingLotDbRepository ??= new AccountingLotDbRepository(_context);

    public IAccountingPeriodDbRepository AccountingPeriodDbRepository =>
        _accountingPeriodDbRepository ??= new AccountingPeriodDbRepository(_context);

    public IInteractiveTxSessionDbRepository InteractiveTxSessionDbRepository =>
        _interactiveTxSessionDbRepository ??= new InteractiveTxSessionDbRepository(_context);

    public IChannelFundingDbRepository ChannelFundingDbRepository =>
        _channelFundingDbRepository ??= new ChannelFundingDbRepository(_context);

    public IChannelPolicyDbRepository ChannelPolicyDbRepository =>
        _channelPolicyDbRepository ??= new ChannelPolicyDbRepository(_context, _timeProvider);

    /// <param name="context">The scope's database context.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="sha256">The hasher the channel repository uses.</param>
    /// <param name="utxoMemoryRepository">The in-memory UTXO set, updated after a successful save.</param>
    /// <param name="timeProvider">The clock that stamps new HTLC rows (<c>HtlcEntity.AddedAt</c>, the start of the
    /// BOLT 4 hold time); <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="maxDustHtlcExposureMsat">The node's <c>Node:MaxDustHtlcExposureMsat</c>, the limit a commitment
    /// snapshot stored without one runs under while it is loaded (NL-290); null keeps the check off.</param>
    public UnitOfWork(NLightningDbContext context, ILogger<UnitOfWork> logger, ISha256 sha256,
                      IUtxoMemoryRepository utxoMemoryRepository, TimeProvider? timeProvider = null,
                      ulong? maxDustHtlcExposureMsat = null, AccountingFeedGate? accountingFeedGate = null)
    {
        _accountingFeedGate = accountingFeedGate;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger;
        _sha256 = sha256;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _utxoMemoryRepository = utxoMemoryRepository;
        _maxDustHtlcExposureMsat = maxDustHtlcExposureMsat;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A channel whose peer has no <c>Peers</c> row (an inbound peer from a loopback address, never saved before
    /// NL-497) is not forgotten: its peer is returned as <see cref="PeerModel.IsInboundOnly"/> with no address, so its
    /// channels are registered and it is never dialed.
    /// </remarks>
    public async Task<ICollection<PeerModel>> GetPeersForStartupAsync()
    {
        var peers = await PeerDbRepository.GetAllAsync();
        var peerList = peers.ToList();

        var known = peerList.Select(p => p.NodeId).ToHashSet();
        var channelPeerIds = await _context.Channels.AsNoTracking()
                                           .Where(c => c.State != (byte)ChannelState.Closed
                                                    && c.State != (byte)ChannelState.Stale)
                                           .Select(c => c.RemoteNodeId)
                                           .Distinct()
                                           .ToListAsync();
        foreach (var nodeId in channelPeerIds.Where(n => !known.Contains(n)))
        {
            _logger.LogWarning("Peer {PeerId} has channels but no saved address; its channels are loaded and we wait "
                             + "for it to connect", nodeId);
            peerList.Add(new PeerModel(nodeId, string.Empty, 0, string.Empty) { IsInboundOnly = true });
        }

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