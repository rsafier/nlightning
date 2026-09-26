namespace NLightning.Domain.Persistence.Interfaces;

using Bitcoin.Interfaces;
using Bitcoin.ValueObjects;
using Bitcoin.Wallet.Models;
using Channels.Interfaces;
using Node.Interfaces;
using Node.Models;
using Onchain.Interfaces;
using Payments.Interfaces;
using Protocol.Onion.Interfaces;

public interface IUnitOfWork : IDisposable
{
    // Bitcoin repositories
    IBlockchainStateDbRepository BlockchainStateDbRepository { get; }
    IWatchedTransactionDbRepository WatchedTransactionDbRepository { get; }
    IWalletAddressesDbRepository WalletAddressesDbRepository { get; }
    IUtxoDbRepository UtxoDbRepository { get; }

    // On-chain repositories (BOLT 5 plan O0)
    IWatchedOutpointDbRepository WatchedOutpointDbRepository { get; }
    IBroadcastTransactionDbRepository BroadcastTransactionDbRepository { get; }
    IBlockHeaderDbRepository BlockHeaderDbRepository { get; }

    // On-chain resolution repositories (BOLT 5 plan O1)
    IRevokedCommitmentDbRepository RevokedCommitmentDbRepository { get; }
    IOnchainResolutionDbRepository OnchainResolutionDbRepository { get; }

    // Chanel repositories
    IChannelConfigDbRepository ChannelConfigDbRepository { get; }
    IChannelDbRepository ChannelDbRepository { get; }
    IChannelKeySetDbRepository ChannelKeySetDbRepository { get; }
    IChannelStateDbRepository ChannelStateDbRepository { get; }
    IRemoteShachainDbRepository RemoteShachainDbRepository { get; }

    // Node repositories
    IPeerDbRepository PeerDbRepository { get; }

    // Payment repositories
    IInvoiceDbRepository InvoiceDbRepository { get; }
    IPaymentDbRepository PaymentDbRepository { get; }
    IForwardCircuitDbRepository ForwardCircuitDbRepository { get; }

    // Onion replay set (NL-078)
    IOnionReplayDbRepository OnionReplayDbRepository { get; }

    Task<ICollection<PeerModel>> GetPeersForStartupAsync();
    void AddUtxo(UtxoModel utxoModel);
    void TrySpendUtxo(TxId transactionId, uint index);

    void SaveChanges();
    Task SaveChangesAsync();
}