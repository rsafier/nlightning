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

public static class ChannelEntityConfiguration
{
    public static void ConfigureChannelEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelEntity>(entity =>
        {
            // Set PrimaryKey
            entity.HasKey(e => e.ChannelId);

            // Set required props
            entity.Property(e => e.FundingOutputIndex).IsRequired();
            entity.Property(e => e.FundingAmountSatoshis).IsRequired();
            entity.Property(e => e.IsInitiator).IsRequired();
            entity.Property(e => e.LocalNextHtlcId).IsRequired();
            entity.Property(e => e.RemoteNextHtlcId).IsRequired();
            entity.Property(e => e.LocalRevocationNumber).IsRequired();
            entity.Property(e => e.RemoteRevocationNumber).IsRequired();
            entity.Property(e => e.LocalCommitmentNumber).IsRequired();
            entity.Property(e => e.RemoteCommitmentNumber).IsRequired();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.Version).IsRequired();
            entity.Property(e => e.LocalBalanceMsat).IsRequired();
            entity.Property(e => e.RemoteBalanceMsat).IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.FundingTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.RemoteNodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();

            // Nullable properties
            entity.Property(e => e.LastSentSignature).IsRequired(false);
            entity.Property(e => e.LastReceivedSignature).IsRequired(false);
            entity.Property(e => e.RemoteAlias)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.ShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired(false);

            // Commitment state (migration AddCommitmentState)
            entity.Property(e => e.RemoteNextPerCommitmentPoint)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired(false);
            entity.Property(e => e.SentCommitDiff).IsRequired(false);
            entity.Property(e => e.LastSentOrder).IsRequired();
            entity.Property(e => e.ErrorSent).IsRequired(false);
            entity.Property(e => e.DataLossDetected).IsRequired();

            // Dust exposure policy of the snapshot (migration AddInvoicesPaymentsAndCircuits, NL-242)
            entity.Property(e => e.MaxDustHtlcExposureMsat).IsRequired(false);
            entity.Property(e => e.RevocationLogFromNumber).IsRequired(false);

            // Mutual close (migration AddShutdownState, BOLT2 plan N10)
            entity.Property(e => e.LocalShutdownScript).IsRequired(false);
            entity.Property(e => e.RemoteShutdownScript).IsRequired(false);
            entity.Property(e => e.ClosingTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.ClosingTransaction).IsRequired(false);

            // Configure the relationship with ChannelConfig (1:1)
            entity.HasOne(e => e.Config)
                  .WithOne()
                  .HasForeignKey<ChannelConfigEntity>(c => c.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Configure the relationship with HTLCs (1:many)
            entity.HasMany(e => e.Htlcs)
                  .WithOne()
                  .HasForeignKey(h => h.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Configure the relationship with the local scid aliases (1:many)
            entity.HasMany(e => e.LocalAliases)
                  .WithOne()
                  .HasForeignKey(a => a.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Configure the relationship with KeySets (1:many)
            entity.HasMany(e => e.KeySets)
                  .WithOne()
                  .HasForeignKey(h => h.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Configure the relationship with WatchedTransactions (1:many)
            entity.HasMany(e => e.WatchedTransactions)
                  .WithOne()
                  .HasForeignKey(wt => wt.ChannelId)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<ChannelEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.FundingTxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.RemoteNodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.LastSentSignature).HasColumnType($"varbinary({CryptoConstants.MaxSignatureSize})");
        entity.Property(e => e.LastReceivedSignature).HasColumnType($"varbinary({CryptoConstants.MaxSignatureSize})");
        entity.Property(e => e.RemoteAlias).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.RemoteNextPerCommitmentPoint)
              .HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.SentCommitDiff).HasColumnType("varbinary(max)");
        entity.Property(e => e.ErrorSent).HasColumnType("varbinary(max)");
        entity.Property(e => e.LocalShutdownScript).HasColumnType("varbinary(max)");
        entity.Property(e => e.RemoteShutdownScript).HasColumnType("varbinary(max)");
        entity.Property(e => e.ClosingTxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.ClosingTransaction).HasColumnType("varbinary(max)");
    }
}