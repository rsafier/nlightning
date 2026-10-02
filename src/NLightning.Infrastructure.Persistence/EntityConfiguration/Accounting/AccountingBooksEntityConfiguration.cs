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
/// The books' tables (NL-602 A2, plan §6.3; per book since migration <c>AddAccountingFinancial</c>, A3-T0):
/// <c>AccountingEntries</c>, <c>AccountingPostings</c>, <c>AccountingBalances</c> and <c>AccountingCursor</c> (one row
/// per book).
/// </summary>
public static class AccountingBooksEntityConfiguration
{
    public static void ConfigureAccountingBooksEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<AccountingEntryEntity>(entity =>
        {
            entity.HasKey(e => new { e.Book, e.LedgerSeq, e.Adjustment });
            entity.Property(e => e.Book).ValueGeneratedNever();
            entity.Property(e => e.LedgerSeq).ValueGeneratedNever();
            entity.Property(e => e.Adjustment).ValueGeneratedNever();

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
            entity.Property(e => e.Flags).IsRequired();
            entity.Property(e => e.Classification).IsRequired(false);
            entity.Property(e => e.RuleId).IsRequired(false);
            entity.Property(e => e.ClosedPeriodId)
                  .HasMaxLength(AccountingSchemaLimits.PeriodIdMaxLength)
                  .IsRequired(false);

            // Each book's projector is its only writer: one entry per sealed key and adjustment (a reversal's lookup
            // and a rebuild check it)
            entity.HasIndex(e => new { e.Book, e.EventKey, e.Adjustment }).IsUnique();
            entity.HasIndex(e => new { e.Book, e.OccurredAt });

            // A period close marks its entries; verify and the lock read them back
            entity.HasIndex(e => new { e.Book, e.ClosedPeriodId });

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<AccountingPostingEntity>(entity =>
        {
            entity.HasKey(p => new { p.Book, p.LedgerSeq, p.Adjustment, p.Index });
            entity.Property(p => p.Book).ValueGeneratedNever();
            entity.Property(p => p.LedgerSeq).ValueGeneratedNever();
            entity.Property(p => p.Adjustment).ValueGeneratedNever();
            entity.Property(p => p.Index).ValueGeneratedNever();
            entity.Property(p => p.Account).IsRequired();
            entity.Property(p => p.AccountName)
                  .HasMaxLength(AccountingSchemaLimits.AccountNameMaxLength)
                  .IsRequired(false);
            entity.Property(p => p.AmountMsat).IsRequired();
            entity.Property(p => p.OccurredAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(p => p.FiatAmount)
                  .HasPrecision(AccountingSchemaLimits.FiatPrecision, AccountingSchemaLimits.FiatScale)
                  .IsRequired(false);
            entity.Property(p => p.FiatCurrency)
                  .HasMaxLength(AccountingSchemaLimits.CurrencyLength)
                  .IsFixedLength()
                  .IsRequired(false);
            entity.Property(p => p.PriceId).IsRequired(false);

            // Period sums per account (income statement, balance at a time)
            entity.HasIndex(p => new { p.Book, p.Account, p.OccurredAt });

            entity.HasOne<AccountingEntryEntity>()
                  .WithMany()
                  .HasForeignKey(p => new { p.Book, p.LedgerSeq, p.Adjustment })
                  .OnDelete(DeleteBehavior.Cascade);

            // The back-valuation's work list (PriceId IS NULL AND Book = 1, oldest first) seeks this index; it also
            // serves the price foreign key (no separate PriceId index), and the operational book's rows, all without a
            // price, are never scanned for it
            entity.HasIndex(p => new { p.PriceId, p.Book, p.OccurredAt });

            // A valued posting keeps its price (reports are reproducible)
            entity.HasOne<AccountingPriceEntity>()
                  .WithMany()
                  .HasForeignKey(p => p.PriceId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountingBalanceEntity>(entity =>
        {
            entity.HasKey(b => new { b.Book, b.Account, b.AccountName });
            entity.Property(b => b.Book).ValueGeneratedNever();
            entity.Property(b => b.Account).ValueGeneratedNever();
            entity.Property(b => b.AccountName)
                  .HasMaxLength(AccountingSchemaLimits.AccountNameMaxLength)
                  .ValueGeneratedNever();
            entity.Property(b => b.BalanceMsat).IsRequired();
            entity.Property(b => b.FiatAmount)
                  .HasPrecision(AccountingSchemaLimits.FiatPrecision, AccountingSchemaLimits.FiatScale)
                  .IsRequired();
        });

        modelBuilder.Entity<AccountingCursorEntity>(entity =>
        {
            entity.HasKey(c => c.Book);
            entity.Property(c => c.Book).ValueGeneratedNever();
            entity.Property(c => c.LastLedgerSeq).IsRequired();
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<AccountingEntryEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}