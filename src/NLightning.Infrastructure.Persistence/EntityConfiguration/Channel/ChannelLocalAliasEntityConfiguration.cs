using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class ChannelLocalAliasEntityConfiguration
{
    public static void ConfigureChannelLocalAliasEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelLocalAliasEntity>(entity =>
        {
            // The alias alone is the key: an alias must never resolve to more than one channel
            entity.HasKey(e => e.Alias);

            entity.Property(e => e.Alias)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ChannelLocalAliasEntity> entity)
    {
        entity.Property(e => e.Alias).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}