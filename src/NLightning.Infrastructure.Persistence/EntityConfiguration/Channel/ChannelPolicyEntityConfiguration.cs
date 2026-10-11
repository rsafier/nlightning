using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class ChannelPolicyEntityConfiguration
{
    public static void ConfigureChannelPolicyEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelPolicyEntity>(entity =>
        {
            // One override per channel (migration AddSpliceFundings, wave sp1 lane SP1-G); no FK to Channels
            entity.HasKey(e => e.ChannelId);

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.FeeBaseMsat).IsRequired(false);
            entity.Property(e => e.FeeProportionalMillionths).IsRequired(false);
            entity.Property(e => e.CltvExpiryDelta).IsRequired(false);
            entity.Property(e => e.HtlcMinimumMsat).IsRequired(false);
            entity.Property(e => e.HtlcMaximumMsat).IsRequired(false);
            entity.Property(e => e.UpdatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ChannelPolicyEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}