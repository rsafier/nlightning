using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Domain.Crypto.Constants;
using Entities.Bitcoin;
using Enums;
using ValueConverters;

public static class BlockHeaderEntityConfiguration
{
    public static void ConfigureBlockHeaderEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<BlockHeaderEntity>(entity =>
        {
            // One row per height of the active chain (migration AddChainWatchAndBroadcasts, BOLT 5 plan O0-T3)
            entity.HasKey(e => e.Height);
            entity.Property(e => e.Height).ValueGeneratedNever();

            entity.Property(e => e.BlockHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.PreviousBlockHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<BlockHeaderEntity> entity)
    {
        entity.Property(e => e.BlockHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.PreviousBlockHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}