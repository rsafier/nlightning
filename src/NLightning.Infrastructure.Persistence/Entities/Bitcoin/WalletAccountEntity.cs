namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.Enums;

public sealed class WalletAccountEntity
{
    public string Name { get; set; } = "";
    public AddressType AddressType { get; set; }
    public uint AccountIndex { get; set; }
    public string ExtendedPublicKey { get; set; } = "";
    public byte[] MasterFingerprint { get; set; } = [];
    public string DerivationPath { get; set; } = "";
    public bool WatchOnly { get; set; }
    public uint BirthdayHeight { get; set; }
    public uint ExternalKeyCount { get; set; }
    public uint InternalKeyCount { get; set; }
}