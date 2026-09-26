using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class RevokedCommitmentEntityConfiguration
{
    public static void ConfigureRevokedCommitmentEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<RevokedCommitmentEntity>(entity =>
        {
            // One row per revoked peer commitment with HTLCs (migration AddOnchainResolution, BOLT 5 plan O1-T1)
            entity.HasKey(e => new { e.ChannelId, e.Number });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Number).IsRequired();
            entity.Property(e => e.FeeratePerKw).IsRequired();
            entity.Property(e => e.LocalMsat).IsRequired();
            entity.Property(e => e.RemoteMsat).IsRequired();
            entity.Property(e => e.Htlcs).IsRequired();

            // No navigation on ChannelEntity: written only by ChannelStateDbRepository
            entity.HasOne<ChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<RevokedCommitmentEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.Htlcs).HasColumnType("varbinary(max)");
    }
}