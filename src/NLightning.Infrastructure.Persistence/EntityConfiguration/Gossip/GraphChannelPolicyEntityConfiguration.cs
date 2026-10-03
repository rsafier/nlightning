using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Gossip;

using Domain.Channels.ValueObjects;
using Entities.Gossip;
using Enums;
using ValueConverters;

public static class GraphChannelPolicyEntityConfiguration
{
    public static void ConfigureGraphChannelPolicyEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<GraphChannelPolicyEntity>(entity =>
        {
            entity.HasKey(e => new { e.ShortChannelId, e.Direction });

            entity.Property(e => e.ShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Direction).IsRequired();
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.MessageFlags).IsRequired();
            entity.Property(e => e.ChannelFlags).IsRequired();
            entity.Property(e => e.CltvExpiryDelta).IsRequired();
            entity.Property(e => e.HtlcMinimumMsat).IsRequired();
            entity.Property(e => e.HtlcMaximumMsat).IsRequired();
            entity.Property(e => e.FeeBaseMsat).IsRequired();
            entity.Property(e => e.FeePpm).IsRequired();
            entity.Property(e => e.RawUpdate).IsRequired();

            // A policy never outlives its channel
            entity.HasOne<GraphChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ShortChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<GraphChannelPolicyEntity> entity)
    {
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.RawUpdate).HasColumnType("varbinary(max)");
    }
}