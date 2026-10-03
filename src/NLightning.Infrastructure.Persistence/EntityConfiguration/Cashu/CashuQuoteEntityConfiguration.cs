using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Cashu;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Cashu.Models;
using Domain.Crypto.Constants;
using Entities.Cashu;
using Enums;
using ValueConverters;

public static class CashuQuoteEntityConfiguration
{
    /// <summary>The longest failure reason stored (longer ones are cut by the repository).</summary>
    public const int FailureReasonMaxLength = 1024;

    public static void ConfigureCashuQuoteEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<CashuQuoteEntity>(entity =>
        {
            entity.HasKey(e => e.QuoteId);

            entity.Property(e => e.QuoteId)
                  .HasMaxLength(CashuQuoteModel.QuoteIdMaxLength)
                  .IsRequired();
            entity.Property(e => e.Method).IsRequired();
            entity.Property(e => e.Direction).IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.MaxFeeMsat).IsRequired(false);
            entity.Property(e => e.FeeMsat).IsRequired(false);
            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(e => e.Address)
                  .HasMaxLength(CashuQuoteModel.AddressMaxLength)
                  .IsRequired(false);
            entity.Property(e => e.Request).IsRequired(false);
            entity.Property(e => e.FeeIndex).IsRequired(false);
            entity.Property(e => e.TxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.OutputIndex).IsRequired(false);
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.FailureReason)
                  .HasMaxLength(FailureReasonMaxLength)
                  .IsRequired(false);
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.UpdatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();

            // Payment events find their melt by hash, deposits their mint quote by address, blocks the pending
            // on-chain melts by method and state
            entity.HasIndex(e => e.PaymentHash);
            entity.HasIndex(e => e.Address);
            entity.HasIndex(e => new { e.Direction, e.Method, e.State });

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<CashuDepositEntity>(entity =>
        {
            entity.HasKey(e => new { e.TxId, e.OutputIndex });

            entity.Property(e => e.TxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.OutputIndex).IsRequired();
            entity.Property(e => e.QuoteId)
                  .HasMaxLength(CashuQuoteModel.QuoteIdMaxLength)
                  .IsRequired();
            entity.Property(e => e.AmountSat).IsRequired();
            entity.Property(e => e.BlockHeight).IsRequired();
            entity.Property(e => e.ReportedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // A quote's deposits, and the deposits not reported yet
            entity.HasIndex(e => e.QuoteId);
            entity.HasIndex(e => e.ReportedAt);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<CashuQuoteEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.TxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<CashuDepositEntity> entity)
    {
        entity.Property(e => e.TxId).HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
    }
}