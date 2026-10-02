using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Accounting.Constants;
using Domain.Crypto.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

public static class OfferEntityConfiguration
{
    public static void ConfigureOfferEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<OfferEntity>(entity =>
        {
            entity.HasKey(e => e.OfferId);

            entity.Property(e => e.OfferId)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.Bolt12).IsRequired();
            entity.Property(e => e.OfferBytes).IsRequired();
            entity.Property(e => e.Description).IsRequired(false);
            entity.Property(e => e.Label)
                  .HasMaxLength(AccountingSchemaLimits.LabelMaxBytes)
                  .IsRequired(false);
            entity.Property(e => e.Tags)
                  .HasMaxLength(AccountingSchemaLimits.TagsMaxBytes)
                  .IsRequired(false);
            entity.Property(e => e.AmountMsat).IsRequired(false);
            entity.Property(e => e.Currency).IsRequired(false);
            entity.Property(e => e.Issuer).IsRequired(false);
            entity.Property(e => e.QuantityMax).IsRequired(false);
            entity.Property(e => e.AbsoluteExpiry)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);
            entity.Property(e => e.Metadata).IsRequired();
            entity.Property(e => e.IssuerKind).IsRequired();
            entity.Property(e => e.HasPaths).IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.DisabledAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // listoffers: newest first, optionally only the active ones
            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => e.Status);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<OfferEntity> entity)
    {
        entity.Property(e => e.OfferId).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
    }
}