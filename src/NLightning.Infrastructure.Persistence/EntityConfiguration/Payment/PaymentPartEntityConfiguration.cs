using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

public static class PaymentPartEntityConfiguration
{
    public static void ConfigurePaymentPartEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<PaymentPartEntity>(entity =>
        {
            entity.HasKey(e => new { e.PaymentHash, e.PartIndex });

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.PartIndex).IsRequired();
            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.HtlcId).IsRequired();
            entity.Property(e => e.State).IsRequired();

            // The part dies with its payment (a retry clears the attempt's parts)
            entity.HasOne<PaymentEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.PaymentHash)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<PaymentPartHopEntity>(entity =>
        {
            entity.HasKey(e => new { e.PaymentHash, e.PartIndex, e.HopIndex });

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.PartIndex).IsRequired();
            entity.Property(e => e.HopIndex).IsRequired();
            entity.Property(e => e.NodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.ShortChannelId)
                  .HasConversion<ShortChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.CltvExpiry).IsRequired();
            entity.Property(e => e.SharedSecret).IsRequired();
            entity.Property(e => e.HoldTimeMs).IsRequired(false);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PaymentPartEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PaymentPartHopEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.SharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }
}