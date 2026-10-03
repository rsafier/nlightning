using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.Contexts;

using Entities.Accounting;
using Entities.Bitcoin;
using Entities.Cashu;
using Entities.Channel;
using Entities.Gossip;
using Entities.LiquidityAds;
using Entities.Node;
using Entities.Onchain;
using Entities.Payment;
using EntityConfiguration.Accounting;
using EntityConfiguration.Bitcoin;
using EntityConfiguration.Cashu;
using EntityConfiguration.Channel;
using EntityConfiguration.Gossip;
using EntityConfiguration.LiquidityAds;
using EntityConfiguration.Node;
using EntityConfiguration.Onchain;
using EntityConfiguration.Payment;
using Enums;
using Providers;

public class NLightningDbContext : DbContext
{
    private readonly DatabaseType _databaseType;

    public NLightningDbContext(DbContextOptions<NLightningDbContext> options, DatabaseTypeProvider databaseTypeProvider)
        : base(options)
    {
        _databaseType = databaseTypeProvider.DatabaseType;
    }

    // Bitcoin DbSets
    public DbSet<BlockchainStateEntity> BlockchainStates { get; set; }
    public DbSet<WatchedTransactionEntity> WatchedTransactions { get; set; }
    public DbSet<WalletAddressEntity> WalletAddresses { get; set; }
    public DbSet<UtxoEntity> Utxos { get; set; }
    public DbSet<WatchedOutpointEntity> WatchedOutpoints { get; set; }
    public DbSet<BroadcastTransactionEntity> BroadcastTransactions { get; set; }
    public DbSet<BlockHeaderEntity> BlockHeaders { get; set; }
    public DbSet<FeeInputReservationEntity> FeeInputReservations { get; set; }
    public DbSet<FeeInputReservationInputEntity> FeeInputReservationInputs { get; set; }

    // Channel DbSets
    public DbSet<ChannelEntity> Channels { get; set; }
    public DbSet<ChannelConfigEntity> ChannelConfigs { get; set; }
    public DbSet<ChannelKeySetEntity> ChannelKeySets { get; set; }
    public DbSet<HtlcEntity> Htlcs { get; set; }
    public DbSet<ChannelLocalAliasEntity> ChannelLocalAliases { get; set; }
    public DbSet<RemoteShachainEntity> RemoteShachains { get; set; }
    public DbSet<CommitmentEntity> Commitments { get; set; }
    public DbSet<FeeUpdateEntity> FeeUpdates { get; set; }
    public DbSet<RevokedCommitmentEntity> RevokedCommitments { get; set; }
    public DbSet<InteractiveTxSessionEntity> InteractiveTxSessions { get; set; }
    public DbSet<ChannelFundingEntity> ChannelFundings { get; set; }
    public DbSet<ChannelPolicyEntity> ChannelPolicies { get; set; }

    // On-chain resolution DbSets
    public DbSet<ChannelCloseEntity> ChannelCloses { get; set; }
    public DbSet<OutputResolutionEntity> OutputResolutions { get; set; }

    // Node DbSets
    public DbSet<PeerEntity> Peers { get; set; }
    public DbSet<PeerStorageBlobEntity> PeerStorageBlobs { get; set; }
    public DbSet<PeerStorageRetrievalEntity> PeerStorageRetrievals { get; set; }

    // Payment DbSets
    public DbSet<InvoiceEntity> Invoices { get; set; }
    public DbSet<PaymentEntity> Payments { get; set; }
    public DbSet<PaymentHopEntity> PaymentHops { get; set; }
    public DbSet<PaymentPartEntity> PaymentParts { get; set; }
    public DbSet<PaymentPartHopEntity> PaymentPartHops { get; set; }
    public DbSet<ForwardCircuitEntity> ForwardCircuits { get; set; }
    public DbSet<OnionReplayEntryEntity> OnionReplayEntries { get; set; }
    public DbSet<OfferEntity> Offers { get; set; }
    public DbSet<TrampolineRelayEntity> TrampolineRelays { get; set; }
    public DbSet<TrampolineRelayPartEntity> TrampolineRelayParts { get; set; }
    public DbSet<TrampolineRelayAttemptEntity> TrampolineRelayAttempts { get; set; }
    public DbSet<PaymentTrampolineHopEntity> PaymentTrampolineHops { get; set; }

    // Accounting feed (NL-602)
    public DbSet<AccountingEventEntity> AccountingEvents { get; set; }

    // Accounting books (NL-602 A2)
    public DbSet<AccountingEntryEntity> AccountingEntries { get; set; }
    public DbSet<AccountingPostingEntity> AccountingPostings { get; set; }
    public DbSet<AccountingBalanceEntity> AccountingBalances { get; set; }
    public DbSet<AccountingCursorEntity> AccountingCursor { get; set; }

    // Accounting financial books (NL-602 A3, migration AddAccountingFinancial)
    public DbSet<AccountingPriceEntity> AccountingPrices { get; set; }
    public DbSet<AccountingRuleEntity> AccountingRules { get; set; }
    public DbSet<AccountingOverrideEntity> AccountingOverrides { get; set; }
    public DbSet<AccountingLotEntity> AccountingLots { get; set; }
    public DbSet<AccountingLotReliefEntity> AccountingLotReliefs { get; set; }
    public DbSet<AccountingPeriodEntity> AccountingPeriods { get; set; }

    // Gossip graph DbSets (BOLT 7 plan G2-T3)
    public DbSet<GraphNodeEntity> GraphNodes { get; set; }
    public DbSet<GraphChannelEntity> GraphChannels { get; set; }
    public DbSet<GraphChannelPolicyEntity> GraphChannelPolicies { get; set; }
    public DbSet<GraphBannedNodeEntity> GraphBannedNodes { get; set; }

    // Liquidity ads purchases (NL-850 LA3, migration AddLiquidityPurchases)
    public DbSet<LiquidityPurchaseEntity> LiquidityPurchases { get; set; }

    // The CDK payment processor's quotes and deposits (NL-997, migration AddCashuProcessorQuotes)
    public DbSet<CashuQuoteEntity> CashuQuotes { get; set; }
    public DbSet<CashuDepositEntity> CashuDeposits { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Bitcoin entities
        modelBuilder.ConfigureBlockchainStateEntity(_databaseType);
        modelBuilder.ConfigureWatchedTransactionEntity(_databaseType);
        modelBuilder.ConfigureWalletAddressEntity(_databaseType);
        modelBuilder.ConfigureUtxoEntity(_databaseType);
        modelBuilder.ConfigureWatchedOutpointEntity(_databaseType);
        modelBuilder.ConfigureBroadcastTransactionEntity(_databaseType);
        modelBuilder.ConfigureBlockHeaderEntity(_databaseType);
        modelBuilder.ConfigureFeeInputReservationEntity(_databaseType);

        // Channel entities
        modelBuilder.ConfigureChannelEntity(_databaseType);
        modelBuilder.ConfigureChannelConfigEntity(_databaseType);
        modelBuilder.ConfigureChannelKeySetEntity(_databaseType);
        modelBuilder.ConfigureHtlcEntity(_databaseType);
        modelBuilder.ConfigureChannelLocalAliasEntity(_databaseType);
        modelBuilder.ConfigureRemoteShachainEntity(_databaseType);
        modelBuilder.ConfigureCommitmentEntity(_databaseType);
        modelBuilder.ConfigureFeeUpdateEntity(_databaseType);
        modelBuilder.ConfigureRevokedCommitmentEntity(_databaseType);
        modelBuilder.ConfigureInteractiveTxSessionEntity(_databaseType);
        modelBuilder.ConfigureChannelFundingEntity(_databaseType);
        modelBuilder.ConfigureChannelPolicyEntity(_databaseType);

        // On-chain resolution entities
        modelBuilder.ConfigureChannelCloseEntity(_databaseType);
        modelBuilder.ConfigureOutputResolutionEntity(_databaseType);

        // Node entities
        modelBuilder.ConfigurePeerEntity(_databaseType);
        modelBuilder.ConfigurePeerStorageBlobEntity(_databaseType);
        modelBuilder.ConfigurePeerStorageRetrievalEntity(_databaseType);

        // Payment entities
        modelBuilder.ConfigureInvoiceEntity(_databaseType);
        modelBuilder.ConfigurePaymentEntity(_databaseType);
        modelBuilder.ConfigurePaymentPartEntity(_databaseType);
        modelBuilder.ConfigureForwardCircuitEntity(_databaseType);
        modelBuilder.ConfigureOnionReplayEntryEntity(_databaseType);
        modelBuilder.ConfigureOfferEntity(_databaseType);
        modelBuilder.ConfigureTrampolineRelayEntities(_databaseType);

        // Accounting feed (NL-602)
        modelBuilder.ConfigureAccountingEventEntity(_databaseType);

        // Accounting books (NL-602 A2)
        modelBuilder.ConfigureAccountingBooksEntities(_databaseType);
        modelBuilder.ConfigureAccountingFinancialEntities(_databaseType);

        // Gossip graph entities
        modelBuilder.ConfigureGraphNodeEntity(_databaseType);
        modelBuilder.ConfigureGraphChannelEntity(_databaseType);
        modelBuilder.ConfigureGraphChannelPolicyEntity(_databaseType);
        modelBuilder.ConfigureGraphBannedNodeEntity(_databaseType);

        // Liquidity ads purchases
        modelBuilder.ConfigureLiquidityPurchaseEntity(_databaseType);
        modelBuilder.ConfigureCashuQuoteEntities(_databaseType);
    }
}