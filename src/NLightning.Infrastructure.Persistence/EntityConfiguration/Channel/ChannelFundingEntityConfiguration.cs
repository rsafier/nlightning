using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class ChannelFundingEntityConfiguration
{
    public static void ConfigureChannelFundingEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelFundingEntity>(entity =>
        {
            // One row per funding output of a channel (migration AddSpliceFundings, splicing plan §3.8)
            entity.HasKey(e => new { e.ChannelId, e.FundingTxId });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.FundingTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.OutputIndex).IsRequired();
            entity.Property(e => e.CapacitySatoshis).IsRequired();
            entity.Property(e => e.LocalFundingPubKey)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.RemoteFundingPubKey)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.LocalFundingKeyIndex).IsRequired();
            entity.Property(e => e.LocalBalanceDeltaMsat).IsRequired();
            entity.Property(e => e.RemoteBalanceDeltaMsat).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.FeeratePerKw).IsRequired(false);
            entity.Property(e => e.Locktime).IsRequired(false);
            entity.Property(e => e.RbfOf)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.ConfirmedHeight).IsRequired(false);
            entity.Property(e => e.ShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.SpliceLockedSent).IsRequired();
            entity.Property(e => e.SpliceLockedReceived).IsRequired();
            entity.Property(e => e.AnnouncementSignaturesReceived).IsRequired();
            entity.Property(e => e.Sequence).IsRequired();

            // No navigation on ChannelEntity: written only by ChannelFundingDbRepository and ChannelDbRepository
            entity.HasOne<ChannelEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ChannelFundingEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.FundingTxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.LocalFundingPubKey).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.RemoteFundingPubKey).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.RbfOf).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
    }
}