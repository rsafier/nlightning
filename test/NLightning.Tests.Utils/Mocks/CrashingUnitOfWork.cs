using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Gossip.Interfaces;
using NLightning.Domain.Node.Interfaces;
using NLightning.Domain.Node.Models;
using NLightning.Domain.Onchain.Interfaces;
using NLightning.Domain.Payments.Interfaces;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Protocol.Onion.Interfaces;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// Thrown by <see cref="CrashingUnitOfWork"/> in place of the save it was told to crash.
/// </summary>
public sealed class SimulatedCrashException(int saveNumber)
    : Exception($"Simulated crash at save {saveNumber}")
{
    public int SaveNumber { get; } = saveNumber;
}

/// <summary>
/// Crash injection for the persist-before-send tests (BOLT2 plan N5-T3): wraps a real <see cref="IUnitOfWork"/> and
/// throws <see cref="SimulatedCrashException"/> instead of performing the <see cref="CrashAtSave"/>-th save (1-based,
/// counting <see cref="SaveChanges"/> and <see cref="SaveChangesAsync"/> together), so nothing staged for that save
/// reaches the database. Every other member is delegated.
/// </summary>
/// <remarks>
/// After a crash the unit of work is in the state a crashed process would leave: discard it and open a new one, as
/// the node does after a restart.
/// </remarks>
public sealed class CrashingUnitOfWork(IUnitOfWork inner, int crashAtSave) : IUnitOfWork
{
    /// <summary>The 1-based save that throws; 0 or less never crashes.</summary>
    public int CrashAtSave { get; } = crashAtSave;

    /// <summary>How many saves were requested so far, including the crashed one.</summary>
    public int SaveCount { get; private set; }

    /// <summary>True once the crash happened.</summary>
    public bool HasCrashed { get; private set; }

    public IBlockchainStateDbRepository BlockchainStateDbRepository => inner.BlockchainStateDbRepository;
    public IWatchedTransactionDbRepository WatchedTransactionDbRepository => inner.WatchedTransactionDbRepository;
    public IWalletAddressesDbRepository WalletAddressesDbRepository => inner.WalletAddressesDbRepository;
    public IUtxoDbRepository UtxoDbRepository => inner.UtxoDbRepository;
    public IWatchedOutpointDbRepository WatchedOutpointDbRepository => inner.WatchedOutpointDbRepository;
    public IBroadcastTransactionDbRepository BroadcastTransactionDbRepository =>
        inner.BroadcastTransactionDbRepository;
    public IBlockHeaderDbRepository BlockHeaderDbRepository => inner.BlockHeaderDbRepository;
    public IRevokedCommitmentDbRepository RevokedCommitmentDbRepository => inner.RevokedCommitmentDbRepository;
    public IOnchainResolutionDbRepository OnchainResolutionDbRepository => inner.OnchainResolutionDbRepository;
    public IChannelConfigDbRepository ChannelConfigDbRepository => inner.ChannelConfigDbRepository;
    public IChannelDbRepository ChannelDbRepository => inner.ChannelDbRepository;
    public IChannelKeySetDbRepository ChannelKeySetDbRepository => inner.ChannelKeySetDbRepository;
    public IChannelStateDbRepository ChannelStateDbRepository => inner.ChannelStateDbRepository;
    public IRemoteShachainDbRepository RemoteShachainDbRepository => inner.RemoteShachainDbRepository;
    public IChannelSigningInfoDbRepository ChannelSigningInfoDbRepository => inner.ChannelSigningInfoDbRepository;
    public IGraphDbRepository GraphDbRepository => inner.GraphDbRepository;
    public IPeerDbRepository PeerDbRepository => inner.PeerDbRepository;
    public IInvoiceDbRepository InvoiceDbRepository => inner.InvoiceDbRepository;
    public IPaymentDbRepository PaymentDbRepository => inner.PaymentDbRepository;
    public IForwardCircuitDbRepository ForwardCircuitDbRepository => inner.ForwardCircuitDbRepository;
    public IOnionReplayDbRepository OnionReplayDbRepository => inner.OnionReplayDbRepository;

    public Task<ICollection<PeerModel>> GetPeersForStartupAsync() => inner.GetPeersForStartupAsync();

    public void AddUtxo(UtxoModel utxoModel) => inner.AddUtxo(utxoModel);

    public void TrySpendUtxo(TxId transactionId, uint index) => inner.TrySpendUtxo(transactionId, index);

    public void SaveChanges()
    {
        ThrowIfCrashPoint();
        inner.SaveChanges();
    }

    public Task SaveChangesAsync()
    {
        ThrowIfCrashPoint();
        return inner.SaveChangesAsync();
    }

    public void Dispose() => inner.Dispose();

    private void ThrowIfCrashPoint()
    {
        if (HasCrashed)
            throw new InvalidOperationException("The unit of work crashed; open a new one");

        SaveCount++;
        if (SaveCount != CrashAtSave)
            return;

        HasCrashed = true;
        throw new SimulatedCrashException(SaveCount);
    }
}