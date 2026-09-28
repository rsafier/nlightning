using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class InteractiveTxSessionEntityConfiguration
{
    public static void ConfigureInteractiveTxSessionEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<InteractiveTxSessionEntity>(entity =>
        {
            // One row per negotiation; an RBF attempt is a new session (migration AddInteractiveTxSessions, IT3-T2)
            entity.HasKey(e => new { e.ChannelId, e.SessionId });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.SessionId).IsRequired();
            entity.Property(e => e.Purpose).IsRequired();
            entity.Property(e => e.IsInitiator).IsRequired();
            entity.Property(e => e.FeeratePerKw).IsRequired();
            entity.Property(e => e.Locktime).IsRequired();
            entity.Property(e => e.Inputs).IsRequired();
            entity.Property(e => e.Outputs).IsRequired();
            entity.Property(e => e.LocalContribution).IsRequired();
            entity.Property(e => e.LocalReservationId).IsRequired(false);
            entity.Property(e => e.ConstructedTx).IsRequired(false);
            entity.Property(e => e.OurWitnesses).IsRequired(false);
            entity.Property(e => e.TheirWitnesses).IsRequired(false);
            entity.Property(e => e.OurSharedInputSignature).IsRequired(false);
            entity.Property(e => e.TheirSharedInputSignature).IsRequired(false);
            entity.Property(e => e.LocalFundingSatoshis).IsRequired(false);
            entity.Property(e => e.TheirCommitmentSignature).IsRequired(false);
            entity.Property(e => e.CommitmentSignedSent).IsRequired();
            entity.Property(e => e.CommitmentSignedReceived).IsRequired();
            entity.Property(e => e.TxSignaturesSent).IsRequired();
            entity.Property(e => e.TxSignaturesReceived).IsRequired();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.CreatedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired();
            entity.Property(e => e.ResolvedAt)
                  .HasConversion<UtcTicksConverter>()
                  .IsRequired(false);

            // The unresolved negotiations are read at startup and on channel_reestablish
            entity.HasIndex(e => e.ResolvedAt);

            if (databaseType == DatabaseType.MicrosoftSql)
                OptimizeConfigurationForSqlServer(entity);
        });
    }

    private static void OptimizeConfigurationForSqlServer(EntityTypeBuilder<InteractiveTxSessionEntity> entity)
    {
        entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
        entity.Property(e => e.Inputs).HasColumnType("varbinary(max)");
        entity.Property(e => e.Outputs).HasColumnType("varbinary(max)");
        entity.Property(e => e.LocalContribution).HasColumnType("varbinary(max)");
        entity.Property(e => e.ConstructedTx).HasColumnType("varbinary(max)");
        entity.Property(e => e.OurWitnesses).HasColumnType("varbinary(max)");
        entity.Property(e => e.TheirWitnesses).HasColumnType("varbinary(max)");
        entity.Property(e => e.OurSharedInputSignature)
              .HasColumnType($"varbinary({CryptoConstants.MaxSignatureSize})");
        entity.Property(e => e.TheirSharedInputSignature)
              .HasColumnType($"varbinary({CryptoConstants.MaxSignatureSize})");
        entity.Property(e => e.TheirCommitmentSignature)
              .HasColumnType($"varbinary({CryptoConstants.MaxSignatureSize})");
    }
}