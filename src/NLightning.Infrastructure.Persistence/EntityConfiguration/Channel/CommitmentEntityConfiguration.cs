using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class CommitmentEntityConfiguration
{
    public static void ConfigureCommitmentEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<CommitmentEntity>(entity =>
        {
            // One row per slot (local current, remote current, remote next) and channel
            entity.HasKey(e => new { e.ChannelId, e.Slot });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Slot).IsRequired();
            entity.Property(e => e.Number).IsRequired();
            entity.Property(e => e.FeeratePerKw).IsRequired();
            entity.Property(e => e.LocalMsat).IsRequired();
            entity.Property(e => e.RemoteMsat).IsRequired();
            entity.Property(e => e.Htlcs).IsRequired();
            entity.Property(e => e.PerCommitmentPoint).IsRequired(false);
            entity.Property(e => e.Signature).IsRequired(false);
            entity.Property(e => e.HtlcSignatures).IsRequired(false);

            // No navigation on ChannelEntity: written only by ChannelStateDbRepository
            entity.HasOne<ChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<CommitmentEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.Htlcs).HasColumnType("varbinary(max)");
        entity.Property(e => e.PerCommitmentPoint).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.Signature).HasColumnType($"varbinary({CryptoConstants.MaxSignatureSize})");
        entity.Property(e => e.HtlcSignatures).HasColumnType("varbinary(max)");
    }
}