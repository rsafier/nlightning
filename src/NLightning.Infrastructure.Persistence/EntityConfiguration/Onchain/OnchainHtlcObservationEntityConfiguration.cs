using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Onchain;

using Domain.Channels.Constants;
using Entities.Onchain;
using Enums;
using ValueConverters;

public static class OnchainHtlcObservationEntityConfiguration
{
    public static void ConfigureOnchainHtlcObservationEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<OnchainHtlcObservationEntity>(entity =>
        {
            // No foreign key: recovery must not forget already observed facts when channel rows are removed.
            entity.HasKey(e => new { e.ChannelId, e.Direction, e.HtlcId, e.Settled });
            entity.Property(e => e.ChannelId).HasConversion<ChannelIdConverter>().IsRequired();
            entity.Property(e => e.Direction).IsRequired();
            entity.Property(e => e.HtlcId).IsRequired();
            entity.Property(e => e.Settled).IsRequired();
            entity.Property(e => e.ObservedAt).HasConversion<UtcTicksConverter>().IsRequired();
            if (databaseType == DatabaseType.MicrosoftSql)
                entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        });
    }
}