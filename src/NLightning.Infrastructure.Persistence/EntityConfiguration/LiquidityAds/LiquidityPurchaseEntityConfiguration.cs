using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.LiquidityAds;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Domain.LiquidityAds.Models;
using Entities.LiquidityAds;
using Enums;
using ValueConverters;

public static class LiquidityPurchaseEntityConfiguration
{
    public static void ConfigureLiquidityPurchaseEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<LiquidityPurchaseEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.FundingTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Role).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.RequestedSat).IsRequired();
            entity.Property(e => e.ContributedSat).IsRequired();
            entity.Property(e => e.RateMinAmountSat).IsRequired();
            entity.Property(e => e.RateMaxAmountSat).IsRequired();
            entity.Property(e => e.RateFundingWeight).IsRequired();
            entity.Property(e => e.RateFeeBasis).IsRequired();
            entity.Property(e => e.RateFeeBaseSat).IsRequired();
            entity.Property(e => e.RateChannelCreationFeeSat).IsRequired();
            entity.Property(e => e.PaymentType).IsRequired();
            entity.Property(e => e.MiningFeeSat).IsRequired();
            entity.Property(e => e.ServiceFeeSat).IsRequired();
            entity.Property(e => e.Signature).IsRequired();
            entity.Property(e => e.FundingScript).IsRequired();
            entity.Property(e => e.PeerNodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.LeaseBlocks).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.LeaseStartHeight).IsRequired(false);
            entity.Property(e => e.ClosedAtHeight).IsRequired(false);
            entity.Property(e => e.ClosedEarly).IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // One purchase per funding attempt; the channel's purchases and the close guard read by channel (the
            // index's prefix), the griefing caps and listliquiditypurchases by status
            entity.HasIndex(e => new { e.ChannelId, e.FundingTxId }).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            // No foreign key to Channels: the record outlives its channel (see the entity's remarks)

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<LiquidityPurchaseEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.FundingTxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.PeerNodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.Signature).HasColumnType($"varbinary({LiquidityPurchaseModel.SignatureLength})");
    }
}