using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Channels.Constants;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

public static class PaymentEntityConfiguration
{
    public static void ConfigurePaymentEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<PaymentEntity>(entity =>
        {
            entity.HasKey(e => e.PaymentHash);

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.Bolt11).IsRequired(false);
            entity.Property(e => e.PayeeNodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.FeeMsat).IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.OutgoingChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired(false);
            entity.Property(e => e.OutgoingHtlcId).IsRequired(false);
            entity.Property(e => e.Preimage).IsRequired(false);
            entity.Property(e => e.FailureCode).IsRequired(false);
            entity.Property(e => e.FailureSourceIndex).IsRequired(false);
            entity.Property(e => e.FailureReason).IsRequired(false);
            entity.Property(e => e.CompletedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // In-flight payments are replayed at startup; listings are newest first
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            entity.HasMany(e => e.Hops)
                  .WithOne()
                  .HasForeignKey(h => h.PaymentHash)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<PaymentHopEntity>(entity =>
        {
            entity.HasKey(e => new { e.PaymentHash, e.HopIndex });

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
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

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PaymentEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.PayeeNodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.OutgoingChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.Preimage).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PaymentHopEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.ShortChannelId).HasColumnType($"varbinary({ShortChannelId.Length})");
        entity.Property(e => e.SharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }
}