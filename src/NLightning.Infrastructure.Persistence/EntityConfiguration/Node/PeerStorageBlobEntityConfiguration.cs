using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Domain.Crypto.Constants;
using Entities.Node;
using Enums;
using ValueConverters;

public static class PeerStorageBlobEntityConfiguration
{
    public static void ConfigurePeerStorageBlobEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<PeerStorageBlobEntity>(entity =>
        {
            entity.HasKey(e => e.NodeId);

            entity.Property(e => e.NodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.Blob)
                  .IsRequired();
            entity.Property(e => e.UpdatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PeerStorageBlobEntity> entity)
    {
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        // 65531 bytes is above varbinary(8000)
        entity.Property(e => e.Blob).HasColumnType("varbinary(max)");
    }
}