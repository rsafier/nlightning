using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class BroadcastTransactionEntityConfiguration
{
    public static void ConfigureBroadcastTransactionEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<BroadcastTransactionEntity>(entity =>
        {
            // Keyed by txid (migration AddChainWatchAndBroadcasts, BOLT 5 plan O0-T1)
            entity.HasKey(e => e.TransactionId);

            entity.Property(e => e.TransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.RawTransaction).IsRequired();
            entity.Property(e => e.Purpose).IsRequired();
            entity.Property(e => e.FeeratePerKw).IsRequired();
            entity.Property(e => e.ReplacesTransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.FirstBroadcastHeight).IsRequired();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.ConfirmedHeight).IsRequired(false);
            entity.Property(e => e.ConfirmedBlockHash)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // Every block reads the pending set; a reorg unconfirms the ones above the fork
            entity.HasIndex(e => e.State);
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => e.ConfirmedHeight);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<BroadcastTransactionEntity> entity)
    {
        entity.Property(e => e.TransactionId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.RawTransaction).HasColumnType("varbinary(max)");
        entity.Property(e => e.ReplacesTransactionId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ConfirmedBlockHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}