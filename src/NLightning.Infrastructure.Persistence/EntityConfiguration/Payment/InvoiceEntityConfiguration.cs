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
            entity.Property(e => e.Bolt11).IsRequired(false);
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.OfferId)
                  .HasConversion<HashConverter>()
                  .IsRequired(false);
            entity.Property(e => e.Bolt12InvoiceBytes).IsRequired(false);
            entity.Property(e => e.CustomRecords).IsRequired(false);
            entity.Property(e => e.InvoiceRequestPayerId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired(false);
            entity.Property(e => e.Quantity).IsRequired(false);
            entity.Property(e => e.PayerNote).IsRequired(false);
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

            // A BOLT 12 invoice belongs to one of our offers, which are never deleted; the per-offer caps count by
            // (OfferId, Status) (BOLT 12 plan D11)
            entity.HasOne<OfferEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.OfferId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.OfferId, e.Status });

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<InvoiceEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.Preimage).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.PaymentSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.OfferId).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.InvoiceRequestPayerId)
              .HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
    }
}