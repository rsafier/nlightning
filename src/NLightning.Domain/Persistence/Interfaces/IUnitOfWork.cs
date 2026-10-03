namespace NLightning.Domain.Persistence.Interfaces;

using Accounting.Books;
using Accounting.Financial;
using Accounting.Interfaces;
using Bitcoin.Interfaces;
using Bitcoin.ValueObjects;
using Bitcoin.Wallet.Models;
using Channels.Interfaces;
using Gossip.Interfaces;
using LiquidityAds.Interfaces;
using Node.Interfaces;
using Node.Models;
using Node.PeerStorage;
using Offers.Interfaces;
using Onchain.Interfaces;
using Payments.Interfaces;
using Protocol.InteractiveTx.Interfaces;
using Protocol.Onion.Interfaces;

public interface IUnitOfWork : IDisposable
{
    // Bitcoin repositories
    IBlockchainStateDbRepository BlockchainStateDbRepository { get; }
    IWatchedTransactionDbRepository WatchedTransactionDbRepository { get; }
    IWalletAddressesDbRepository WalletAddressesDbRepository { get; }
    IUtxoDbRepository UtxoDbRepository { get; }

    // Fee input reservations (BOLT 5 plan O7-T1)
    IFeeInputReservationDbRepository FeeInputReservationDbRepository { get; }

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

    // The signer's view of a stored channel (NL-067)
    IChannelSigningInfoDbRepository ChannelSigningInfoDbRepository { get; }

    // BOLT 7 graph (migration AddGossipGraph)
    IGraphDbRepository GraphDbRepository { get; }

    // Node repositories
    IPeerDbRepository PeerDbRepository { get; }

    // BOLT 1 peer storage (migration AddPeerStorage)
    IPeerStorageDbRepository PeerStorageDbRepository { get; }

    // The peer_storage_retrievals our peers sent us (NL-432, migration AddPeerStorageRetrievals); the default is for
    // test doubles that keep none
    IPeerStorageRetrievalDbRepository PeerStorageRetrievalDbRepository =>
        throw new NotSupportedException("This unit of work does not store peer storage retrievals.");

    // Payment repositories
    IInvoiceDbRepository InvoiceDbRepository { get; }
    IPaymentDbRepository PaymentDbRepository { get; }

    // The offered parts of in-flight payments (NL-321, migration AddPaymentParts); the default is for test doubles
    // that store no part rows
    IPaymentPartDbRepository PaymentPartDbRepository =>
        throw new NotSupportedException("This unit of work does not store payment parts.");

    IForwardCircuitDbRepository ForwardCircuitDbRepository { get; }

    // Onion replay set (NL-078)
    IOnionReplayDbRepository OnionReplayDbRepository { get; }

    // BOLT 12 offers (NL-447, migration AddBolt12Offers); the default is for test doubles that store no offers
    IOfferDbRepository OfferDbRepository =>
        throw new NotSupportedException("This unit of work does not store BOLT 12 offers.");

    // Interactive-tx negotiations (splicing plan wave IT, migration AddInteractiveTxSessions of lane IT-C); the default
    // is for units of work and test doubles that store none until that lane lands
    IInteractiveTxSessionDbRepository InteractiveTxSessionDbRepository =>
        throw new NotSupportedException("This unit of work does not store interactive-tx sessions.");

    // Per-channel routing policy overrides (wave sp1 lane SP1-G, table in lane SP1-C's migration); the default is for
    // units of work and test doubles that store none until that lane lands
    IChannelPolicyDbRepository ChannelPolicyDbRepository =>
        throw new NotSupportedException("This unit of work does not store channel policy overrides.");

    // Channel fundings, per-funding commitments and dual-funding columns (splicing plan SP1-C, migration
    // AddSpliceFundings); the default is for test doubles that store none
    IChannelFundingDbRepository ChannelFundingDbRepository =>
        throw new NotSupportedException("This unit of work does not store channel fundings.");

    // The accounting feed (NL-602, migration AddAccountingEvents); the default is for test doubles that store none:
    // writes go nowhere
    IAccountingEventDbRepository AccountingEventDbRepository => NullAccountingEventDbRepository.Instance;

    // The operational books (NL-602 A2, migration AddAccountingBooks); the default is for test doubles that store none
    IAccountingBooksDbRepository AccountingBooksDbRepository =>
        throw new NotSupportedException("This unit of work does not store the accounting books.");

    // The financial books (NL-602 A3, migration AddAccountingFinancial): prices, classification rules, overrides,
    // cost-basis lots and periods; the defaults are for test doubles that store none
    IAccountingPriceDbRepository AccountingPriceDbRepository =>
        throw new NotSupportedException("This unit of work does not store accounting prices.");

    IAccountingRuleDbRepository AccountingRuleDbRepository =>
        throw new NotSupportedException("This unit of work does not store accounting rules.");

    IAccountingOverrideDbRepository AccountingOverrideDbRepository =>
        throw new NotSupportedException("This unit of work does not store accounting overrides.");

    IAccountingLotDbRepository AccountingLotDbRepository =>
        throw new NotSupportedException("This unit of work does not store accounting lots.");

    IAccountingPeriodDbRepository AccountingPeriodDbRepository =>
        throw new NotSupportedException("This unit of work does not store accounting periods.");

    // Liquidity ads purchases, bought and sold (NL-850 LA3, migration AddLiquidityPurchases); the default is for test
    // doubles that store none
    ILiquidityPurchaseDbRepository LiquidityPurchaseDbRepository =>
        throw new NotSupportedException("This unit of work does not store liquidity purchases.");

    /// <summary>
    /// Every saved peer with its channels, plus a peer marked <see cref="PeerModel.IsInboundOnly"/> for every channel
    /// (not Closed or Stale) whose peer has no saved row (NL-497): startup registers every such channel and dials only
    /// the peers with an address.
    /// </summary>
    Task<ICollection<PeerModel>> GetPeersForStartupAsync();
    void AddUtxo(UtxoModel utxoModel);
    void TrySpendUtxo(TxId transactionId, uint index);

    void SaveChanges();
    Task SaveChangesAsync();
}