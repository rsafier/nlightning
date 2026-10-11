using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class ChannelSignerGuardEntityConfiguration
{
    public static void ConfigureChannelSignerGuardEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelSignerGuardEntity>(entity =>
        {
            // One row per channel the local signer ever signed for (migration AddChannelSignerGuards, NL-1345)
            entity.HasKey(e => e.ChannelId);

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.LocalCommitmentNumber).IsRequired();
            entity.Property(e => e.RevokedCommitmentNumber).IsRequired(false);
            entity.Property(e => e.RemoteSignedCommitmentNumber).IsRequired(false);
            entity.Property(e => e.BroadcastSignedCommitmentNumber).IsRequired(false);
            entity.Property(e => e.DataLossDetected).IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        });
    }
}