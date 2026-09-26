using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class ChannelConfigEntityConfiguration
{
    public static void ConfigureChannelConfigEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelConfigEntity>(entity =>
        {
            // Set PrimaryKey
            entity.HasKey(e => e.ChannelId);

            // Set required props
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.MinimumDepth).IsRequired();
            entity.Property(e => e.LocalToSelfDelay).IsRequired();
            entity.Property(e => e.RemoteToSelfDelay).IsRequired();
            entity.Property(e => e.LocalMaxAcceptedHtlcs).IsRequired();
            entity.Property(e => e.RemoteMaxAcceptedHtlcs).IsRequired();
            entity.Property(e => e.LocalDustLimitAmountSats).IsRequired();
            entity.Property(e => e.RemoteDustLimitAmountSats).IsRequired();
            entity.Property(e => e.LocalHtlcMinimumMsat).IsRequired();
            entity.Property(e => e.RemoteHtlcMinimumMsat).IsRequired();
            entity.Property(e => e.LocalChannelReserveAmountSats).IsRequired();
            entity.Property(e => e.RemoteChannelReserveAmountSats).IsRequired();
            entity.Property(e => e.LocalMaxHtlcValueInFlightMsat).IsRequired();
            entity.Property(e => e.RemoteMaxHtlcValueInFlightMsat).IsRequired();
            entity.Property(e => e.FeeRatePerKwSatoshis).IsRequired();
            entity.Property(e => e.OptionAnchorOutputs).IsRequired();
            entity.Property(e => e.HasInferredParams).IsRequired();
            entity.Property(e => e.AnnounceChannel).IsRequired();

            // Nullable byte[] properties
            entity.Property(e => e.LocalUpfrontShutdownScript).IsRequired(false);
            entity.Property(e => e.RemoteUpfrontShutdownScript).IsRequired(false);

            if (databaseType == DatabaseType.MicrosoftSql)
            {
                OptimizeConfigurationForSqlServer(entity);
            }
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ChannelConfigEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.LocalUpfrontShutdownScript).HasColumnType("varbinary(max)");
        entity.Property(e => e.RemoteUpfrontShutdownScript).HasColumnType("varbinary(max)");
    }
}