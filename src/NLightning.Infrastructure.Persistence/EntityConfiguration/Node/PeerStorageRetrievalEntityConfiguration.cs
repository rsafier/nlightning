using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Domain.Crypto.Constants;
using Entities.Node;
using Enums;
using ValueConverters;

public static class PeerStorageRetrievalEntityConfiguration
{
    public static void ConfigurePeerStorageRetrievalEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<PeerStorageRetrievalEntity>(entity =>
        {
            entity.HasKey(e => e.NodeId);

            entity.Property(e => e.NodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.ReceivedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.Blob)
                  .IsRequired();
            entity.Property(e => e.MatchesLastSent);
            entity.Property(e => e.UnknownChannelIds)
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PeerStorageRetrievalEntity> entity)
    {
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        // 65531 bytes is above varbinary(8000); a blob can name up to about 1000 channels of 32 bytes
        entity.Property(e => e.Blob).HasColumnType("varbinary(max)");
        entity.Property(e => e.UnknownChannelIds).HasColumnType("varbinary(max)");
    }
}