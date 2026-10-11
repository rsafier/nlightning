using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class WatchedOutpointEntityConfiguration
{
    public static void ConfigureWatchedOutpointEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<WatchedOutpointEntity>(entity =>
        {
            // Keyed by the outpoint (migration AddChainWatchAndBroadcasts, BOLT 5 plan O0-T2)
            entity.HasKey(e => new { e.TransactionId, e.OutputIndex });

            entity.Property(e => e.TransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.OutputIndex).IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Purpose).IsRequired();
            entity.Property(e => e.SpentByTransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.SpentAtHeight).IsRequired(false);
            entity.Property(e => e.SpentBlockHash)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // Startup loads the watches of a channel's outputs; a reorg clears the spends above the fork
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => e.SpentAtHeight);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<WatchedOutpointEntity> entity)
    {
        entity.Property(e => e.TransactionId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.SpentByTransactionId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.SpentBlockHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}