using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.Contexts;

using Entities.Bitcoin;
using Entities.Channel;
using Entities.Gossip;
using Entities.Node;
using Entities.Onchain;
using Entities.Payment;
using EntityConfiguration.Bitcoin;
using EntityConfiguration.Channel;
using EntityConfiguration.Gossip;
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

    // On-chain resolution DbSets
    public DbSet<ChannelCloseEntity> ChannelCloses { get; set; }
    public DbSet<OutputResolutionEntity> OutputResolutions { get; set; }

    // Node DbSets
    public DbSet<PeerEntity> Peers { get; set; }

    // Payment DbSets
    public DbSet<InvoiceEntity> Invoices { get; set; }
    public DbSet<PaymentEntity> Payments { get; set; }
    public DbSet<PaymentHopEntity> PaymentHops { get; set; }
    public DbSet<ForwardCircuitEntity> ForwardCircuits { get; set; }
    public DbSet<OnionReplayEntryEntity> OnionReplayEntries { get; set; }

    // Gossip graph DbSets (BOLT 7 plan G2-T3)
    public DbSet<GraphNodeEntity> GraphNodes { get; set; }
    public DbSet<GraphChannelEntity> GraphChannels { get; set; }
    public DbSet<GraphChannelPolicyEntity> GraphChannelPolicies { get; set; }
    public DbSet<GraphBannedNodeEntity> GraphBannedNodes { get; set; }

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

        // On-chain resolution entities
        modelBuilder.ConfigureChannelCloseEntity(_databaseType);
        modelBuilder.ConfigureOutputResolutionEntity(_databaseType);

        // Node entities
        modelBuilder.ConfigurePeerEntity(_databaseType);

        // Payment entities
        modelBuilder.ConfigureInvoiceEntity(_databaseType);
        modelBuilder.ConfigurePaymentEntity(_databaseType);
        modelBuilder.ConfigureForwardCircuitEntity(_databaseType);
        modelBuilder.ConfigureOnionReplayEntryEntity(_databaseType);

        // Gossip graph entities
        modelBuilder.ConfigureGraphNodeEntity(_databaseType);
        modelBuilder.ConfigureGraphChannelEntity(_databaseType);
        modelBuilder.ConfigureGraphChannelPolicyEntity(_databaseType);
        modelBuilder.ConfigureGraphBannedNodeEntity(_databaseType);
    }
}