using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Node;

using Entities.Node;
using Enums;

public static class MacaroonRootKeyEntityConfiguration
{
    private const int NonceLength = 24;
    private const int CiphertextLength = 48;

    public static void ConfigureMacaroonRootKeyEntity(this ModelBuilder modelBuilder, DatabaseType databaseType)
    {
        modelBuilder.Entity<MacaroonRootKeyEntity>(entity =>
        {
            // One row per LND root key id (NL-1389)
            entity.HasKey(e => e.RootKeyId);
            entity.Property(e => e.RootKeyId).ValueGeneratedNever();
            entity.Property(e => e.Nonce).HasMaxLength(NonceLength).IsRequired();
            entity.Property(e => e.Ciphertext).HasMaxLength(CiphertextLength).IsRequired();
            entity.Property(e => e.CreatedAt).IsRequired();

            if (databaseType == DatabaseType.MicrosoftSql)
            {
                entity.Property(e => e.Nonce).HasColumnType($"varbinary({NonceLength})");
                entity.Property(e => e.Ciphertext).HasColumnType($"varbinary({CiphertextLength})");
            }
        });
    }
}