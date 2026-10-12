using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Onchain;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Channel;
using Entities.Onchain;
using Enums;
using ValueConverters;

public static class ChannelCloseEntityConfiguration
{
    public static void ConfigureChannelCloseEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelCloseEntity>(entity =>
        {
            // One funding spend per channel (migration AddOnchainResolution, BOLT 5 plan O1-T2)
            entity.HasKey(e => e.ChannelId);

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.CommitmentTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.CommitmentNumber).IsRequired(false);
            entity.Property(e => e.SpentAtHeight).IsRequired();
            entity.Property(e => e.BlockHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            entity.HasOne<ChannelEntity>()
                  .WithOne()
                  .HasForeignKey<ChannelCloseEntity>(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ChannelCloseEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.CommitmentTxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.BlockHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}