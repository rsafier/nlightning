using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Channels.Constants;
using Domain.Protocol.Onion.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

public static class OnionReplayEntryEntityConfiguration
{
    public static void ConfigureOnionReplayEntryEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<OnionReplayEntryEntity>(entity =>
        {
            // One row per packet HMAC (migration AddOnionReplaySet, NL-078)
            entity.HasKey(e => e.Hmac);
            entity.Property(e => e.Hmac)
                  .ValueGeneratedNever()
                  .IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.HtlcId).IsRequired();
            entity.Property(e => e.ExpiryHeight).IsRequired();

            // Pruning deletes the entries the chain passed
            entity.HasIndex(e => e.ExpiryHeight);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<OnionReplayEntryEntity> entity)
    {
        entity.Property(e => e.Hmac).HasColumnType($"varbinary({OnionConstants.HmacLength})");
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}