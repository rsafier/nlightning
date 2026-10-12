using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Channel;

using Domain.Bitcoin.Transactions.Constants;
using Domain.Channels.Constants;
using Domain.Crypto.Constants;
using Entities.Channel;
using Enums;
using ValueConverters;

public static class ChannelSignerBroadcastSessionEntityConfiguration
{
    public static void ConfigureChannelSignerBroadcastSessionEntity(this ModelBuilder modelBuilder,
                                                                    DatabaseType databaseType)
    {
        modelBuilder.Entity<ChannelSignerBroadcastSessionEntity>(entity =>
        {
            // One row per verification nonce signed for broadcast (migration AddSignerBroadcastSessions, NL-1376): the
            // key is what makes a second session's claim fail in every provider
            entity.HasKey(e => new { e.ChannelId, e.NonceFundingTxId, e.CommitmentNumber });

            entity.Property(e => e.ChannelId)
                  .HasConversion<ChannelIdConverter>()
                  .IsRequired();
            entity.Property(e => e.NonceFundingTxId)
                  .HasConversion<TxIdConverter>()
                  .IsRequired();
            entity.Property(e => e.CommitmentNumber).IsRequired();
            entity.Property(e => e.SessionDigest)
                  .HasConversion<HashConverter>()
                  .IsRequired();

            if (databaseType != DatabaseType.MicrosoftSql)
                return;

            entity.Property(e => e.ChannelId).HasColumnType($"varbinary({ChannelConstants.ChannelIdLength})");
            entity.Property(e => e.NonceFundingTxId)
                  .HasColumnType($"varbinary({TransactionConstants.TxIdLength})");
            entity.Property(e => e.SessionDigest).HasColumnType($"varbinary({CryptoConstants.Sha256HashLen})");
        });
    }
}