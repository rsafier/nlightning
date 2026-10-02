using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Accounting;

using Domain.Accounting.Constants;
using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Accounting;
using Enums;
using ValueConverters;

/// <summary>
/// The operational books' tables (NL-602 A2, plan §6.3): <c>AccountingEntries</c>, <c>AccountingPostings</c>,
/// <c>AccountingBalances</c> and the single-row <c>AccountingCursor</c>.
/// </summary>
public static class AccountingBooksEntityConfiguration
{
    public static void ConfigureAccountingBooksEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<AccountingEntryEntity>(entity =>
        {
            entity.HasKey(e => e.LedgerSeq);
            entity.Property(e => e.LedgerSeq).ValueGeneratedNever();

            entity.Property(e => e.EventKey)
                  .HasMaxLength(AccountingEventKeys.MaxLength)
                  .IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.OccurredAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(e => e.Note).IsRequired(false);

            // The projector is the only writer: one entry per sealed key (a reversal's lookup and a rebuild check it)
            entity.HasIndex(e => e.EventKey).IsUnique();
            entity.HasIndex(e => e.OccurredAt);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<AccountingPostingEntity>(entity =>
        {
            entity.HasKey(p => new { p.LedgerSeq, p.Index });
            entity.Property(p => p.LedgerSeq).ValueGeneratedNever();
            entity.Property(p => p.Index).ValueGeneratedNever();
            entity.Property(p => p.Account).IsRequired();
            entity.Property(p => p.AmountMsat).IsRequired();
            entity.Property(p => p.OccurredAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // Period sums per account (income statement, balance at a time)
            entity.HasIndex(p => new { p.Account, p.OccurredAt });

            entity.HasOne<AccountingEntryEntity>()
                  .WithMany()
                  .HasForeignKey(p => p.LedgerSeq)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountingBalanceEntity>(entity =>
        {
            entity.HasKey(b => b.Account);
            entity.Property(b => b.Account).ValueGeneratedNever();
            entity.Property(b => b.BalanceMsat).IsRequired();
        });

        modelBuilder.Entity<AccountingCursorEntity>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.Property(c => c.LastLedgerSeq).IsRequired();
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<AccountingEntryEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}