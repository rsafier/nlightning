using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class RemoteShachainEntityConfiguration
{
    public static void ConfigureRemoteShachainEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<RemoteShachainEntity>(entity =>
        {
            // One row per bucket and channel
            entity.HasKey(e => new { e.ChannelId, e.Bucket });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Bucket).IsRequired();
            entity.Property(e => e.Index).IsRequired();
            entity.Property(e => e.Secret).IsRequired();

            // No navigation on ChannelEntity: the shachain is written by its own repository, never by the channel
            // graph sync in ChannelDbRepository.UpdateAsync
            entity.HasOne<ChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<RemoteShachainEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.Secret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }
}