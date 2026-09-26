using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Gossip;

using Domain.Crypto.Constants;
using Entities.Gossip;
using Enums;
using ValueConverters;

public static class GraphNodeEntityConfiguration
{
    public static void ConfigureGraphNodeEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<GraphNodeEntity>(entity =>
        {
            entity.HasKey(e => e.NodeId);

            entity.Property(e => e.NodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.Features).IsRequired();
            entity.Property(e => e.Alias).IsRequired();
            entity.Property(e => e.Color).IsRequired();
            entity.Property(e => e.Addresses).IsRequired();
            entity.Property(e => e.RawAnnouncement).IsRequired();
            entity.Property(e => e.ReceivedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<GraphNodeEntity> entity)
    {
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.Features).HasColumnType("varbinary(max)");
        entity.Property(e => e.Alias).HasColumnType("varbinary(32)");
        entity.Property(e => e.Color).HasColumnType("varbinary(3)");
        entity.Property(e => e.Addresses).HasColumnType("varbinary(max)");
        entity.Property(e => e.RawAnnouncement).HasColumnType("varbinary(max)");
    }
}