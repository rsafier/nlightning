using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Entities.Gossip;
using Enums;
using ValueConverters;

public static class GraphChannelEntityConfiguration
{
    public static void ConfigureGraphChannelEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<GraphChannelEntity>(entity =>
        {
            entity.HasKey(e => e.ShortChannelId);

            entity.Property(e => e.ShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.NodeId1)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.NodeId2)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.BitcoinKey1)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.BitcoinKey2)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.CapacitySat).IsRequired();
            entity.Property(e => e.Features).IsRequired();
            entity.Property(e => e.RawAnnouncement).IsRequired();
            entity.Property(e => e.Verification).IsRequired();
            entity.Property(e => e.SpentAtHeight).IsRequired(false);
            entity.Property(e => e.ReceivedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // A node's channels (pathfinding load, pruning of nodes without channels) and the spent-channel pruner
            entity.HasIndex(e => e.NodeId1);
            entity.HasIndex(e => e.NodeId2);
            entity.HasIndex(e => e.SpentAtHeight);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<GraphChannelEntity> entity)
    {
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.NodeId1).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.NodeId2).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.BitcoinKey1).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.BitcoinKey2).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.Features).HasColumnType("varbinary(max)");
        entity.Property(e => e.RawAnnouncement).HasColumnType("varbinary(max)");
    }
}