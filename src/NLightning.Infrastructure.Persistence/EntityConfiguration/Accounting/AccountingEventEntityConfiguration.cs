using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Accounting;

using Domain.Accounting.Constants;
using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Entities.Accounting;
using Enums;
using ValueConverters;

public static class AccountingEventEntityConfiguration
{
    private const int HashLength = 32;

    public static void ConfigureAccountingEventEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<AccountingEventEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();

            entity.Property(e => e.EventKey)
                  .HasMaxLength(AccountingEventKeys.MaxLength)
                  .IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.OccurredAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.BlockHeight).IsRequired(false);
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.ShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(e => e.TxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.OutputIndex).IsRequired(false);
            entity.Property(e => e.Counterparty)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired(false);
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.FeeMsat).IsRequired();
            entity.Property(e => e.Finality).IsRequired();
            entity.Property(e => e.Flags).IsRequired();
            entity.Property(e => e.Details).IsRequired(false);
            entity.Property(e => e.LedgerSeq).IsRequired(false);
            entity.Property(e => e.Hash).IsRequired(false);

            // The sealer finds a key's sealed row and the unsealed rows; readers page by ledger sequence and filter by
            // time and channel
            entity.HasIndex(e => e.EventKey);
            entity.HasIndex(e => e.LedgerSeq);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.ChannelId);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<AccountingEventEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.TxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
        entity.Property(e => e.Counterparty).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.Hash).HasColumnType($"varbinary({HashLength})");
    }
}