using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Crypto.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

public static class InvoiceEntityConfiguration
{
    public static void ConfigureInvoiceEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<InvoiceEntity>(entity =>
        {
            entity.HasKey(e => e.PaymentHash);

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.Preimage).IsRequired();
            entity.Property(e => e.PaymentSecret).IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired(false);
            entity.Property(e => e.Description).IsRequired(false);
            entity.Property(e => e.Bolt11).IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.ExpirySeconds).IsRequired();
            entity.Property(e => e.MinFinalCltvExpiry).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.AmountReceivedMsat).IsRequired(false);
            entity.Property(e => e.SettledAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // Newest-first listing
            entity.HasIndex(e => e.CreatedAt);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<InvoiceEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.Preimage).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.PaymentSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }
}