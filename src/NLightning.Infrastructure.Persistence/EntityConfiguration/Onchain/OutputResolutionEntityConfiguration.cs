using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Onchain;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Entities.Channel;
using Entities.Onchain;
using Enums;
using ValueConverters;

public static class OutputResolutionEntityConfiguration
{
    public static void ConfigureOutputResolutionEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<OutputResolutionEntity>(entity =>
        {
            // Keyed by the outpoint (migration AddOnchainResolution, BOLT 5 plan O1-T2)
            entity.HasKey(e => new { e.TransactionId, e.OutputIndex });

            entity.Property(e => e.TransactionId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.OutputIndex).IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.Descriptor).IsRequired();
            entity.Property(e => e.DescriptorData).IsRequired();
            entity.Property(e => e.HtlcDirection).IsRequired(false);
            entity.Property(e => e.HtlcId).IsRequired(false);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.ResolvingTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.WaitUntilHeight).IsRequired(false);
            entity.Property(e => e.DeadlineHeight).IsRequired(false);
            entity.Property(e => e.ResolvedHeight).IsRequired(false);
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // The resolver loads a channel's outputs and, on every block, the unresolved ones
            entity.HasIndex(e => e.ChannelId);
            entity.HasIndex(e => e.State);

            entity.HasOne<ChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<OutputResolutionEntity> entity)
    {
        entity.Property(e => e.TransactionId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.DescriptorData).HasColumnType("varbinary(max)");
        entity.Property(e => e.ResolvingTxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
    }
}