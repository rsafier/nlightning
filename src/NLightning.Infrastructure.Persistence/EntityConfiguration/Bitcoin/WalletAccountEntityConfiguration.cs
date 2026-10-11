using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Persistence.EntityConfiguration.Bitcoin;

using Entities.Bitcoin;
using Enums;

public static class WalletAccountEntityConfiguration
{
    public static void ConfigureWalletAccountEntity(this ModelBuilder modelBuilder, DatabaseType _)
    {
        modelBuilder.Entity<WalletAccountEntity>(entity =>
        {
            entity.HasKey(e => e.Name);
            entity.Property(e => e.Name).HasMaxLength(128);
            entity.Property(e => e.ExtendedPublicKey).HasMaxLength(128).IsRequired();
            entity.Property(e => e.MasterFingerprint).IsRequired();
            entity.Property(e => e.DerivationPath).HasMaxLength(128).IsRequired();
        });
    }
}