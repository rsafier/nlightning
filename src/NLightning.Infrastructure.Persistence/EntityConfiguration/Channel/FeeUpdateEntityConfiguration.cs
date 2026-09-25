using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class FeeUpdateEntityConfiguration
{
    public static void ConfigureFeeUpdateEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<FeeUpdateEntity>(entity =>
        {
            entity.HasKey(e => new { e.ChannelId, e.Sequence });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Sequence).IsRequired();
            entity.Property(e => e.FeeratePerKw).IsRequired();
            entity.Property(e => e.State).IsRequired();

            // No navigation on ChannelEntity: written only by ChannelStateDbRepository
            entity.HasOne<ChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<FeeUpdateEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}