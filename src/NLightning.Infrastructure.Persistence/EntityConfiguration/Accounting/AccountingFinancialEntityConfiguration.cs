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
/// The financial books' own tables (NL-602 A3, migration <c>AddAccountingFinancial</c>): <c>AccountingPrices</c>
/// (A3-T2), <c>AccountingRules</c> and <c>AccountingOverrides</c> (A3-T3), <c>AccountingLots</c> and
/// <c>AccountingLotReliefs</c> (A3-T4) and <c>AccountingPeriods</c> (A3-T5). Fiat values are <c>decimal(28,8)</c>
/// (D-A11; TEXT on SQLite, so never ordered or summed in SQL there).
/// </summary>
public static class AccountingFinancialEntityConfiguration
{
    public static void ConfigureAccountingFinancialEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<AccountingPriceEntity>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedOnAdd();
            entity.Property(p => p.Currency)
                  .HasMaxLength(AccountingSchemaLimits.CurrencyLength)
                  .IsFixedLength()
                  .IsRequired();
            entity.Property(p => p.Time)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(p => p.Price)
                  .HasPrecision(AccountingSchemaLimits.FiatPrecision, AccountingSchemaLimits.FiatScale)
                  .IsRequired();
            // The column keeps the name it had when the property was called Source (NL-708)
            entity.Property(p => p.PriceSource)
                  .HasColumnName(databaseType == DatabaseType.PostgreSql ? "source" : "Source")
                  .IsRequired();
            entity.Property(p => p.FetchedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // One price per currency and time; the nearest-at-or-before lookup seeks this index
            entity.HasIndex(p => new { p.Currency, p.Time }).IsUnique();
        });

        modelBuilder.Entity<AccountingRuleEntity>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();
            entity.Property(r => r.Priority).IsRequired();
            entity.Property(r => r.Kinds)
                  .HasMaxLength(AccountingSchemaLimits.RuleKindsMaxLength)
                  .IsRequired(false);
            entity.Property(r => r.LabelPattern)
                  .HasMaxLength(AccountingSchemaLimits.LabelPatternMaxLength)
                  .IsRequired(false);
            entity.Property(r => r.TagKey)
                  .HasMaxLength(AccountingSchemaLimits.TagKeyMaxLength)
                  .IsRequired(false);
            entity.Property(r => r.TagValue)
                  .HasMaxLength(AccountingSchemaLimits.TagValueMaxBytes)
                  .IsRequired(false);
            entity.Property(r => r.Counterparty)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired(false);
            entity.Property(r => r.OfferId)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(r => r.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(r => r.TargetAccount)
                  .HasMaxLength(AccountingSchemaLimits.AccountNameMaxLength)
                  .IsRequired();
            entity.Property(r => r.Enabled).IsRequired();
            entity.Property(r => r.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(r => r.Description)
                  .HasMaxLength(AccountingSchemaLimits.NoteMaxLength)
                  .IsRequired(false);

            // Match order
            entity.HasIndex(r => new { r.Priority, r.Id });

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<AccountingOverrideEntity>(entity =>
        {
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).ValueGeneratedOnAdd();
            entity.Property(o => o.EventKey)
                  .HasMaxLength(AccountingEventKeys.MaxLength)
                  .IsRequired();
            entity.Property(o => o.Account)
                  .HasMaxLength(AccountingSchemaLimits.AccountNameMaxLength)
                  .IsRequired();
            entity.Property(o => o.Note)
                  .HasMaxLength(AccountingSchemaLimits.NoteMaxLength)
                  .IsRequired(false);
            entity.Property(o => o.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            entity.HasIndex(o => o.EventKey).IsUnique();
        });

        modelBuilder.Entity<AccountingLotEntity>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).ValueGeneratedNever();
            entity.Property(l => l.AcquiredAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(l => l.Origin).IsRequired();
            entity.Property(l => l.SourceLedgerSeq).IsRequired(false);
            entity.Property(l => l.SourceAdjustment).IsRequired();
            entity.Property(l => l.Account).IsRequired(false);
            entity.Property(l => l.ParentLotId).IsRequired(false);
            entity.Property(l => l.HeldSince)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);
            entity.Property(l => l.Lender).IsRequired(false);
            entity.Property(l => l.OriginalMsat).IsRequired();
            entity.Property(l => l.RemainingMsat).IsRequired();
            entity.Property(l => l.FiatCost)
                  .HasPrecision(AccountingSchemaLimits.FiatPrecision, AccountingSchemaLimits.FiatScale)
                  .IsRequired(false);
            entity.Property(l => l.FiatCurrency)
                  .HasMaxLength(AccountingSchemaLimits.CurrencyLength)
                  .IsFixedLength()
                  .IsRequired(false);
            entity.Property(l => l.PriceId).IsRequired(false);
            entity.Property(l => l.BasisEstimated).IsRequired();
            entity.Property(l => l.ClosedPeriodId)
                  .HasMaxLength(AccountingSchemaLimits.PeriodIdMaxLength)
                  .IsRequired(false);

            // The open lots in acquisition order (FIFO/LIFO); the lots an entry opened; a close's lots
            entity.HasIndex(l => new { l.RemainingMsat, l.AcquiredAt });
            entity.HasIndex(l => new { l.SourceLedgerSeq, l.SourceAdjustment });
            entity.HasIndex(l => l.ClosedPeriodId);

            entity.HasOne<AccountingPriceEntity>()
                  .WithMany()
                  .HasForeignKey(l => l.PriceId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountingLotReliefEntity>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();
            entity.Property(r => r.LotId).IsRequired();
            entity.Property(r => r.LedgerSeq).IsRequired();
            entity.Property(r => r.Adjustment).IsRequired();
            entity.Property(r => r.RelievedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(r => r.Msat).IsRequired();
            entity.Property(r => r.FiatCostRelieved)
                  .HasPrecision(AccountingSchemaLimits.FiatPrecision, AccountingSchemaLimits.FiatScale)
                  .IsRequired(false);
            entity.Property(r => r.Proceeds)
                  .HasPrecision(AccountingSchemaLimits.FiatPrecision, AccountingSchemaLimits.FiatScale)
                  .IsRequired(false);
            entity.Property(r => r.ClosedPeriodId)
                  .HasMaxLength(AccountingSchemaLimits.PeriodIdMaxLength)
                  .IsRequired(false);
            entity.Property(r => r.Kind).IsRequired();

            // The reliefs of a disposing entry; realized gains by period; a close's reliefs
            entity.HasIndex(r => new { r.LedgerSeq, r.Adjustment });
            entity.HasIndex(r => r.RelievedAt);
            entity.HasIndex(r => r.ClosedPeriodId);

            entity.HasOne<AccountingLotEntity>()
                  .WithMany()
                  .HasForeignKey(r => r.LotId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AccountingPeriodEntity>(entity =>
        {
            entity.HasKey(p => p.PeriodId);
            entity.Property(p => p.PeriodId)
                  .HasMaxLength(AccountingSchemaLimits.PeriodIdMaxLength)
                  .ValueGeneratedNever();
            entity.Property(p => p.Start)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(p => p.End)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(p => p.State).IsRequired();
            entity.Property(p => p.ClosedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);
            entity.Property(p => p.LastLedgerSeq).IsRequired();
            entity.Property(p => p.ChainHash).IsRequired(false);
            entity.Property(p => p.Digest).IsRequired(false);
            entity.Property(p => p.Signature).IsRequired(false);
            entity.Property(p => p.Forced).IsRequired();
            entity.Property(p => p.ClosingState).IsRequired(false);

            // The lock's lookup (the closed period that holds a time) and the last close
            entity.HasIndex(p => new { p.State, p.End });

            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(p => p.ChainHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
                entity.Property(p => p.Digest).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
                // The signature stays varbinary(max): D-A13 signs with the node key's compact signature (64 bytes), but a
                // later recoverable or DER form needs no schema change
            }
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<AccountingRuleEntity> entity)
    {
        entity.Property(r => r.Counterparty).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(r => r.OfferId).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(r => r.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }
}