using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Gossip;

using Domain.Crypto.Constants;
using Entities.Gossip;
using Enums;
using ValueConverters;

public static class GraphBannedNodeEntityConfiguration
{
    public static void ConfigureGraphBannedNodeEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<GraphBannedNodeEntity>(entity =>
        {
            entity.HasKey(e => e.NodeId);

            entity.Property(e => e.NodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.Reason)
                  .HasMaxLength(256)
                  .IsRequired();
            entity.Property(e => e.Until)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<GraphBannedNodeEntity> entity)
    {
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
    }
}