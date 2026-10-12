using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Entities.Channel;

public static class ChannelKeyIndexHighWaterEntityConfiguration
{
    public static void ConfigureChannelKeyIndexHighWaterEntity(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelKeyIndexHighWaterEntity>(entity =>
        {
            // One row, id 1 (NL-1388)
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.HighWater).IsRequired();
        });
    }
}