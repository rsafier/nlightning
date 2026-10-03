using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Payment;

using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Payment;
using Enums;
using ValueConverters;

/// <summary>
/// The trampoline relay tables (NL-875, migration <c>AddTrampolineRelays</c>): <c>TrampolineRelays</c>,
/// <c>TrampolineRelayParts</c> and the payer side's <c>PaymentTrampolineHops</c>.
/// </summary>
public static class TrampolineRelayEntityConfiguration
{
    public static void ConfigureTrampolineRelayEntities(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<TrampolineRelayEntity>(entity =>
        {
            entity.HasKey(e => e.PaymentHash);

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.NextNodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired(false);
            entity.Property(e => e.NextEncryptedRecipientData).IsRequired(false);
            entity.Property(e => e.NextPathKey).IsRequired(false);
            entity.Property(e => e.RecipientFeatures).IsRequired(false);
            entity.Property(e => e.RecipientBlindedPaths).IsRequired(false);
            entity.Property(e => e.NextTrampolinePacket).IsRequired(false);
            entity.Property(e => e.AmountOutMsat).IsRequired();
            entity.Property(e => e.CltvExpiryOut).IsRequired();
            entity.Property(e => e.IncomingTotalMsat).IsRequired();
            entity.Property(e => e.FeeEarnedMsat).IsRequired(false);
            entity.Property(e => e.OutgoingPaymentSecret).IsRequired(false);
            entity.Property(e => e.Preimage).IsRequired(false);
            entity.Property(e => e.FailureCode).IsRequired(false);
            entity.Property(e => e.FailureReason).IsRequired(false);
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.CompletedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // The startup replay reads the unfinished relays; listings are newest first
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<TrampolineRelayPartEntity>(entity =>
        {
            // Keyed by the incoming HTLC: the switch and the resolvers look a part up by it
            entity.HasKey(e => new { e.ChannelId, e.HtlcId });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.HtlcId).IsRequired();
            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.CltvExpiry).IsRequired();
            entity.Property(e => e.OuterSharedSecret).IsRequired();
            entity.Property(e => e.TrampolineSharedSecret).IsRequired();
            entity.Property(e => e.OuterPaymentSecret).IsRequired(false);

            // A part dies with its relay (the foreign key's index serves the relay's part list)
            entity.HasOne<TrampolineRelayEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.PaymentHash)
                  .OnDelete(DeleteBehavior.Cascade);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });

        modelBuilder.Entity<PaymentTrampolineHopEntity>(entity =>
        {
            entity.HasKey(e => new { e.PaymentHash, e.Attempt, e.HopIndex });

            entity.Property(e => e.PaymentHash)
                  .HasConversion<HashConverter>()
                  .IsRequired();
            entity.Property(e => e.Attempt).IsRequired();
            entity.Property(e => e.HopIndex).IsRequired();
            entity.Property(e => e.NodeId)
                  .HasConversion<CompactPubKeyConverter>()
                  .IsRequired();
            entity.Property(e => e.SharedSecret).IsRequired();
            entity.Property(e => e.AmountMsat).IsRequired();
            entity.Property(e => e.CltvExpiry).IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<TrampolineRelayEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.NextNodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.NextPathKey).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.OutgoingPaymentSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.Preimage).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<TrampolineRelayPartEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.OuterSharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.TrampolineSharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
        entity.Property(e => e.OuterPaymentSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<PaymentTrampolineHopEntity> entity)
    {
        entity.Property(e => e.PaymentHash).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        entity.Property(e => e.NodeId).HasColumnType($"varbinary({CryptoConstants.CompactPubkeyLen})");
        entity.Property(e => e.SharedSecret).HasColumnType($"varbinary({CryptoConstants.SecretLen})");
    }
}